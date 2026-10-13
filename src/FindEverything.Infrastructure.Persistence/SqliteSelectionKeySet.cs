using FindEverything.Application.Results;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>Reference-counted immutable private key database. UI membership lookups have a bounded cache.</summary>
internal sealed class SqliteSelectionKeySet : IQuerySelectionKeySet
{
    private sealed class Shared(string path, long count)
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
        public readonly LinkedList<string> Recent = new();
        public readonly Dictionary<string, LinkedListNode<string>> CacheNodes = new(StringComparer.Ordinal);
        public readonly SqliteConnection Connection = Open(path);
        public readonly string Path = path;
        public readonly long Count = count;
        public int References = 1;
        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
    }
    private Shared? _shared;
    public SqliteSelectionKeySet(string path, long count) => _shared = new(path, count);
    private SqliteSelectionKeySet(Shared shared) => _shared = shared;
    public long Count => SharedValue.Count;
    public string DatabasePath => SharedValue.Path;
    private Shared SharedValue => _shared ?? throw new ObjectDisposedException(nameof(SqliteSelectionKeySet));
    public bool Contains(string stableKey)
        => ContainsCore(stableKey, "stable_key", "s:");
    public bool ContainsQueryKey(string key) => ContainsCore(key, "query_key", "q:");
    private bool ContainsCore(string stableKey, string column, string prefix)
    {
        var shared = SharedValue;
        var key = stableKey.ToUpperInvariant();
        lock (shared.Gate)
        {
            if (shared.Cache.TryGetValue(prefix + key, out var cached))
            {
                Cache(shared, prefix + key, cached);
                return cached;
            }
            using var command = shared.Connection.CreateCommand();
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM selection_baseline WHERE {column}=$key);";
            command.Parameters.AddWithValue("$key", key);
            var result = Convert.ToInt64(command.ExecuteScalar()) != 0;
            Cache(shared, prefix + key, result);
            return result;
        }
    }
    public Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) => ReadMembershipCoreAsync(keys, "stable_key", "s:", cancellationToken);
    public Task<IReadOnlySet<string>> ReadQueryMembershipAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) => ReadMembershipCoreAsync(keys, "query_key", "q:", cancellationToken);

    private Task<IReadOnlySet<string>> ReadMembershipCoreAsync(IReadOnlyList<string> keys,
        string column, string prefix, CancellationToken token)
    {
        var lease = Retain();
        // Microsoft.Data.Sqlite's async commands may run synchronously. This
        // boundary guarantees page membership never opens a DB on the UI thread.
        return Task.Run<IReadOnlySet<string>>(() =>
        {
            using (lease)
            {
                token.ThrowIfCancellationRequested();
                var shared = ((SqliteSelectionKeySet)lease).SharedValue;
                var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();
                lock (shared.Gate)
                    foreach (var key in keys.Select(value => value.ToUpperInvariant()).Distinct(StringComparer.Ordinal))
                    {
                        if (shared.Cache.TryGetValue(prefix + key, out var found))
                        {
                            Cache(shared, prefix + key, found);
                            if (found) matches.Add(key);
                        }
                        else missing.Add(key);
                    }
                if (missing.Count == 0) return matches;
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = shared.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                connection.Open();
                using var interrupt = new SqliteReadCancellation(connection, token);
                foreach (var batch in missing.Chunk(500))
                {
                    token.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    var parameters = new string[batch.Length];
                    for (var index = 0; index < batch.Length; index++)
                    {
                        parameters[index] = "$key" + index;
                        command.Parameters.AddWithValue(parameters[index], batch[index]);
                    }
                    command.CommandText = $"SELECT {column} FROM selection_baseline WHERE {column} IN ({string.Join(',', parameters)});";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) matches.Add(reader.GetString(0));
                    lock (shared.Gate)
                        foreach (var key in batch) Cache(shared, prefix + key, matches.Contains(key));
                }
                return matches;
            }
        }); // Always enter the delegate so its retained lease is released after cancellation.
    }

    private static void Cache(Shared shared, string key, bool value)
    {
        if (shared.CacheNodes.Remove(key, out var old)) shared.Recent.Remove(old);
        shared.Cache[key] = value;
        shared.CacheNodes[key] = shared.Recent.AddLast(key);
        while (shared.Cache.Count > 8192)
        {
            var expired = shared.Recent.First!.Value;
            shared.Recent.RemoveFirst(); shared.CacheNodes.Remove(expired); shared.Cache.Remove(expired);
        }
    }
    public IQuerySelectionKeySet Retain()
    {
        var shared = SharedValue;
        lock (shared.Gate) { checked { shared.References++; } }
        return new SqliteSelectionKeySet(shared);
    }
    public void Dispose()
    {
        var shared = Interlocked.Exchange(ref _shared, null);
        if (shared is null) return;
        lock (shared.Gate)
        {
            if (--shared.References != 0) return;
            shared.Connection.Dispose();
            try { File.Delete(shared.Path); } catch (IOException) { }
            try { Directory.Delete(System.IO.Path.GetDirectoryName(shared.Path)!); } catch (IOException) { }
        }
        GC.SuppressFinalize(this);
    }
    ~SqliteSelectionKeySet() { try { Dispose(); } catch { /* Process-exit cleanup is best effort. */ } }
}
