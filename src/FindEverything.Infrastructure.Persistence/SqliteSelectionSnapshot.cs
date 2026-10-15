using FindEverything.Application.Results;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>Freezes a predicate-relative selection into bounded-memory, immutable keys.</summary>
public static class SqliteSelectionSnapshot
{
    public enum SetOperation { Union, Except, SymmetricDifference }

    /// <summary>Combines stable selected keys with the current result set, entirely on disk.</summary>
    public static Task<IQuerySelectionKeySet> CombineAsync(QuerySelection existing, IQuerySelectionKeySet current,
        SetOperation operation, CancellationToken token = default, Func<string, string>? queryKeyMapper = null) => Task.Run(() =>
    {
        if (existing.AllMatching) throw new ArgumentException("Normalize a predicate-relative selection first.", nameof(existing));
        var directory = Directory.CreateTempSubdirectory("FindEverything-selection-").FullName;
        var path = Path.Combine(directory, "selection.db");
        try
        {
            long count;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var interrupt = new SqliteReadCancellation(connection, token);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; CREATE TABLE selection_baseline(stable_key TEXT PRIMARY KEY COLLATE BINARY,query_key TEXT NOT NULL UNIQUE COLLATE BINARY) WITHOUT ROWID;";
                command.ExecuteNonQuery();
                void Attach(string file, string name)
                {
                    command.CommandText = $"ATTACH DATABASE $path AS {name};";
                    command.Parameters.Clear(); command.Parameters.AddWithValue("$path", file); command.ExecuteNonQuery();
                }
                Attach(current.DatabasePath, "current_set");
                if (existing.Baseline is { } baseline)
                {
                    Attach(baseline.DatabasePath, "previous_set");
                    command.CommandText = "INSERT INTO selection_baseline SELECT * FROM previous_set.selection_baseline;";
                    command.Parameters.Clear(); command.ExecuteNonQuery();
                }
                using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
                foreach (var key in existing.Keys)
                {
                    token.ThrowIfCancellationRequested();
                    command.Parameters.Clear(); command.Parameters.AddWithValue("$key", key.ToUpperInvariant());
                    command.CommandText = "DELETE FROM selection_baseline WHERE stable_key=$key;";
                    if (command.ExecuteNonQuery() == 0)
                    {
                        command.Parameters.AddWithValue("$query", (queryKeyMapper?.Invoke(key) ?? key).ToUpperInvariant());
                        command.CommandText = "INSERT INTO selection_baseline(stable_key,query_key) VALUES($key,$query);";
                        command.ExecuteNonQuery();
                    }
                }
                foreach (var key in existing.UnavailableKeys)
                {
                    token.ThrowIfCancellationRequested();
                    command.Parameters.Clear(); command.Parameters.AddWithValue("$key", key.ToUpperInvariant());
                    command.CommandText = "DELETE FROM selection_baseline WHERE stable_key=$key;"; command.ExecuteNonQuery();
                }
                command.Parameters.Clear();
                command.CommandText = operation switch
                {
                    SetOperation.Union => "INSERT OR IGNORE INTO selection_baseline SELECT * FROM current_set.selection_baseline;",
                    SetOperation.Except => "DELETE FROM selection_baseline WHERE stable_key IN (SELECT stable_key FROM current_set.selection_baseline);",
                    _ => "CREATE TEMP TABLE overlap AS SELECT stable_key FROM selection_baseline INTERSECT SELECT stable_key FROM current_set.selection_baseline; INSERT OR IGNORE INTO selection_baseline SELECT * FROM current_set.selection_baseline; DELETE FROM selection_baseline WHERE stable_key IN (SELECT stable_key FROM overlap);"
                };
                command.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); transaction.Commit();
                command.Transaction = null; command.CommandText = "SELECT COUNT(*) FROM selection_baseline;";
                count = Convert.ToInt64(command.ExecuteScalar());
            }
            return (IQuerySelectionKeySet)new SqliteSelectionKeySet(path, count);
        }
        catch
        {
            try { File.Delete(path); Directory.Delete(directory); } catch (IOException) { }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw;
        }
    }, token);

    public static Task<IQuerySelectionKeySet> CreateAsync<T>(IResultQuerySession<T> query,
        QuerySelection selection, Func<T, string> stableKey, Func<T, string> queryKey,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("FindEverything-selection-").FullName;
        var path = Path.Combine(directory, "selection.db");
        try
        {
            long count;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; CREATE TABLE selection_baseline(stable_key TEXT PRIMARY KEY COLLATE BINARY,query_key TEXT NOT NULL UNIQUE COLLATE BINARY) WITHOUT ROWID;";
                create.ExecuteNonQuery();
                using var transaction = connection.BeginTransaction();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO selection_baseline(stable_key,query_key) VALUES($stable,$query);";
                var stable = insert.Parameters.Add("$stable", SqliteType.Text);
                var original = insert.Parameters.Add("$query", SqliteType.Text);
                using var captured = selection.Capture();
                async IAsyncEnumerable<ResultSelectionKey> ReadKeys()
                {
                    if (query is IResultSelectionKeyReader keyReader)
                    {
                        await foreach (var key in keyReader.EnumerateSelectionKeysAsync(captured, cancellationToken).ConfigureAwait(false)) yield return key;
                    }
                    else
                        await foreach (var item in query.EnumerateSelectionAsync(captured, cancellationToken).ConfigureAwait(false))
                            yield return new(stableKey(item), queryKey(item));
                }
                await foreach (var item in ReadKeys().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stable.Value = item.StableKey.ToUpperInvariant();
                    original.Value = item.QueryKey.ToUpperInvariant();
                    insert.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                using var total = connection.CreateCommand();
                total.CommandText = "SELECT COUNT(*) FROM selection_baseline;";
                count = Convert.ToInt64(total.ExecuteScalar());
            }
            return (IQuerySelectionKeySet)new SqliteSelectionKeySet(path, count);
        }
        catch
        {
            try { File.Delete(path); Directory.Delete(directory); } catch (IOException) { }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw;
        }
    }, cancellationToken);
}
