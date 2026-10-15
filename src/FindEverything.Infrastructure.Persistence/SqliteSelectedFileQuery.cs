using System.Runtime.CompilerServices;
using System.Text.Json;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>
/// A fixed, disk-backed review of selected file metadata. The source is streamed
/// once; scrolling never restarts the source query or retains all row objects.
/// </summary>
public sealed class SqliteSelectedFileQuery : IResultQuerySession<IndexedPathEntry>, IResultSelectionKeyReader
{
    private const int BatchSize = 500;
    private readonly IResultQuerySession<IndexedPathEntry> _source;
    private readonly long _count;
    private readonly SemaphoreSlim _reads = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    private SqliteSelectedFileQuery(IResultQuerySession<IndexedPathEntry> source, string path, long count, string identity)
    {
        _source = source;
        DatabasePath = path;
        _count = count;
        Identity = identity;
    }

    public string Identity { get; }
    internal string DatabasePath { get; }

    /// <summary>Owns <paramref name="source"/> only after this task returns successfully.</summary>
    public static async Task<IResultQuerySession<IndexedPathEntry>> CreateAsync(
        IResultQuerySession<IndexedPathEntry> source, QuerySelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        using var captured = selection.Capture();
        var identity = source.Identity;
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var directory = Directory.CreateTempSubdirectory("FindEverything-selected-files-").FullName;
            var path = Path.Combine(directory, "results.db");
            try
            {
                long count = 0;
                using (var connection = Open(path, readOnly: false))
                using (var interrupt = new SqliteReadCancellation(connection, token))
                {
                    using var create = connection.CreateCommand();
                    create.CommandText = """
                        PRAGMA journal_mode=OFF;
                        PRAGMA synchronous=OFF;
                        CREATE TABLE selected_files(
                            ordinal INTEGER PRIMARY KEY,
                            stable_key TEXT NOT NULL,
                            query_key TEXT NOT NULL,
                            payload TEXT NOT NULL);
                        """;
                    create.ExecuteNonQuery();
                    using var transaction = connection.BeginTransaction();
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO selected_files(ordinal,stable_key,query_key,payload) VALUES($ordinal,$stable,$query,$payload);";
                    var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
                    var stable = insert.Parameters.Add("$stable", SqliteType.Text);
                    var query = insert.Parameters.Add("$query", SqliteType.Text);
                    var payload = insert.Parameters.Add("$payload", SqliteType.Text);
                    insert.Prepare();
                    await foreach (var row in source.EnumerateSelectionAsync(captured, token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        ordinal.Value = count;
                        stable.Value = row.StableKey ?? row.FullPath;
                        query.Value = row.FullPath;
                        payload.Value = JsonSerializer.Serialize(row);
                        insert.ExecuteNonQuery();
                        count = checked(count + 1);
                    }
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                token.ThrowIfCancellationRequested();
                return (IResultQuerySession<IndexedPathEntry>)new SqliteSelectedFileQuery(source, path, count, identity);
            }
            catch
            {
                DeletePrivateDatabase(path);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw;
            }
        }, token).ConfigureAwait(false);
    }

    public Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_count);
    }

    public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(offset));
        return ExecuteReadAsync((connection, token) =>
        {
            using var command = PageCommand(connection, "payload", offset, limit);
            using var reader = command.ExecuteReader();
            var rows = new List<IndexedPathEntry>(limit);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                rows.Add(JsonSerializer.Deserialize<IndexedPathEntry>(reader.GetString(0))
                    ?? throw new InvalidDataException("The selected file snapshot contains an empty row."));
            }
            return new ResultPage<IndexedPathEntry>(rows, offset, (long)offset + rows.Count < _count);
        }, cancellationToken);
    }

    public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        for (var offset = 0; ;)
        {
            var page = await ReadRangeAsync(offset, BatchSize, cancellationToken).ConfigureAwait(false);
            var membership = await captured.ReadMembershipAsync(
                page.Items.Select(row => row.StableKey ?? row.FullPath).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < page.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (membership[index]) yield return page.Items[index];
            }
            if (!page.HasMore) yield break;
            offset = checked(offset + page.Items.Count);
        }
    }

    public async IAsyncEnumerable<ResultSelectionKey> EnumerateSelectionKeysAsync(QuerySelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        for (var offset = 0; ;)
        {
            var page = await ReadKeysAsync(offset, cancellationToken).ConfigureAwait(false);
            var membership = await captured.ReadMembershipAsync(
                page.Items.Select(row => row.StableKey).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < page.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (membership[index]) yield return page.Items[index];
            }
            if (!page.HasMore) yield break;
            offset = checked(offset + page.Items.Count);
        }
    }

    public async Task<long> CountSelectionAsync(QuerySelection selection, CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        if (captured.Baseline is null && captured.Keys.Count == 0 && captured.UnavailableKeys.Count == 0)
        {
            var total = await CountAsync(cancellationToken).ConfigureAwait(false);
            return captured.AllMatching ? total : 0;
        }
        long count = 0;
        await foreach (var _ in EnumerateSelectionKeysAsync(captured, cancellationToken).ConfigureAwait(false)) count++;
        return count;
    }

    private Task<ResultPage<ResultSelectionKey>> ReadKeysAsync(int offset, CancellationToken cancellationToken) =>
        ExecuteReadAsync((connection, token) =>
        {
            using var command = PageCommand(connection, "stable_key,query_key", offset, BatchSize);
            using var reader = command.ExecuteReader();
            var keys = new List<ResultSelectionKey>(BatchSize);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                keys.Add(new(reader.GetString(0), reader.GetString(1)));
            }
            return new ResultPage<ResultSelectionKey>(keys, offset, (long)offset + keys.Count < _count);
        }, cancellationToken);

    private static SqliteCommand PageCommand(SqliteConnection connection, string columns, int offset, int limit)
    {
        var command = connection.CreateCommand();
        // Seek the ordinal primary key rather than walking OFFSET preceding rows.
        command.CommandText = $"SELECT {columns} FROM selected_files WHERE ordinal >= $offset ORDER BY ordinal LIMIT $limit;";
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit);
        return command;
    }

    private async Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, CancellationToken, T> read, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _reads.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                using var connection = Open(DatabasePath, readOnly: true);
                using var interrupt = new SqliteReadCancellation(connection, linked.Token);
                try { return read(connection, linked.Token); }
                catch (SqliteException) when (linked.IsCancellationRequested) { throw new OperationCanceledException(linked.Token); }
            }, linked.Token).ConfigureAwait(false);
        }
        finally { _reads.Release(); }
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        await _reads.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await _source.DisposeAsync().ConfigureAwait(false); }
            finally { await Task.Run(() => DeletePrivateDatabase(DatabasePath)).ConfigureAwait(false); }
        }
        finally { _reads.Release(); _lifetime.Dispose(); }
    }

    private static void DeletePrivateDatabase(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
        try { Directory.Delete(Path.GetDirectoryName(path)!); } catch (IOException) { }
    }
}
