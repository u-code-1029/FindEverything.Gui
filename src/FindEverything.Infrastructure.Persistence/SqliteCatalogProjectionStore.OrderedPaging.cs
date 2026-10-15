using System.Security.Cryptography;
using System.Text;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    private sealed partial class ProjectionQuery
    {
        private readonly OrderedPageCache _orderedPages = new();
        private readonly OrderedPageCache _selectedPages = new();
        private long _orderedKeyBuildCount;

        // SQLite owns/deletes the private temporary file. Only path keys are retained,
        // never field payloads. File-backed temp storage has a 2 MiB page-cache budget.
        // Ordinals make arbitrary/deep viewport reads seek instead of repeating a sort
        // or walking every preceding match. Each fixed query/selection builds once.
        private sealed class OrderedPageCache
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);
            internal SqliteConnection? Connection;
            internal string? SelectionSignature;

            internal async ValueTask DisposeAsync()
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try { Connection?.Dispose(); Connection = null; }
                finally { Gate.Release(); }
            }
        }

        private async Task<ResultPage<CatalogProjectionRow>> ReadOrderedRangeAsync(QuerySelection? selection,
            int offset, int limit, CancellationToken token)
        {
            var cache = selection is null ? _orderedPages : _selectedPages;
            var signature = selection is null ? null : SelectionSignature(selection, token);
            await cache.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                token.ThrowIfCancellationRequested();
                if (cache.Connection is null || cache.SelectionSignature != signature)
                {
                    cache.Connection?.Dispose(); cache.Connection = null;
                    var connection = _owner.OpenConnection(readOperation: true);
                    try
                    {
                        using var interrupt = new SqliteReadCancellation(connection, token);
                        ConfigureOrderedStorage(connection);
                        if (selection is not null) PrepareSelectionTables(connection, selection, token);
                        await MaterializeOrderedKeysAsync(connection, "user_inputs", selection,
                            checkPublication: true, token).ConfigureAwait(false);
                        cache.Connection = connection;
                        cache.SelectionSignature = signature;
                    }
                    catch { connection.Dispose(); throw; }
                }
                using var readInterrupt = new SqliteReadCancellation(cache.Connection, token);
                return await ReadPageAsync(cache.Connection, "user_inputs", null, offset, limit,
                    checkPublication: true, previewCharacters: _query.TextPreviewCharacters, token,
                    orderedKeys: true).ConfigureAwait(false);
            }
            finally { cache.Gate.Release(); }
        }

        private static void ConfigureOrderedStorage(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA temp_store=FILE; PRAGMA temp.cache_size=-2048;";
            command.ExecuteNonQuery();
        }

        private static void PrepareSelectionTables(SqliteConnection connection, QuerySelection selection, CancellationToken token)
        {
            if (selection.Baseline is { } baseline)
            {
                using var attach = connection.CreateCommand();
                attach.CommandText = "ATTACH DATABASE $path AS selection_source;";
                attach.Parameters.AddWithValue("$path", baseline.DatabasePath); attach.ExecuteNonQuery();
            }
            using var prepare = connection.CreateCommand();
            prepare.CommandText = "CREATE TEMP TABLE selection_keys(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID; CREATE TEMP TABLE selection_unavailable(path_key TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID;";
            prepare.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction(deferred: true);
            prepare.Transaction = transaction;
            foreach (var (table, keys) in new[] { ("selection_keys", selection.Keys), ("selection_unavailable", selection.UnavailableKeys) })
            {
                prepare.CommandText = $"INSERT OR IGNORE INTO {table}(path_key) VALUES($key);";
                prepare.Parameters.Clear(); var parameter = prepare.Parameters.Add("$key", SqliteType.Text);
                prepare.Prepare();
                foreach (var key in keys) { token.ThrowIfCancellationRequested(); parameter.Value = PathKey(key); prepare.ExecuteNonQuery(); }
            }
            transaction.Commit();
        }

        private async Task MaterializeOrderedKeysAsync(SqliteConnection connection, string notesTable,
            QuerySelection? selection, bool checkPublication, CancellationToken token)
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            EnsureGeneration(connection, transaction, checkPublication);
            using var create = connection.CreateCommand(); create.Transaction = transaction;
            create.CommandText = "CREATE TEMP TABLE ordered_keys(ordinal INTEGER PRIMARY KEY, path_key TEXT NOT NULL COLLATE BINARY);";
            await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            using var populate = BuildCommand(connection, transaction, notesTable, selection, null, null);
            populate.CommandText = "INSERT INTO temp.ordered_keys(path_key) SELECT r.path_key "
                + populate.CommandText + " ORDER BY " + BuildOrdering() + ";";
            await populate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            Interlocked.Increment(ref _orderedKeyBuildCount);
        }

        private SqliteCommand BuildOrderedPageCommand(SqliteConnection connection, SqliteTransaction transaction, int offset, int limit)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$generation", _generation);
            command.Parameters.AddWithValue("$profile", ProfileKey(_scope.ProfileId));
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$limit", limit + 1);
            // CROSS JOIN pins the rank range as the outer loop. The row PK lookup
            // cannot cause SQLite to sort the generation again on a deep page.
            command.CommandText = "FROM temp.ordered_keys k CROSS JOIN projection_rows r "
                + "ON r.generation=$generation AND r.path_key=k.path_key "
                + "WHERE k.ordinal>$offset ORDER BY k.ordinal LIMIT $limit;";
            return command;
        }

        private static string SelectionSignature(QuerySelection selection, CancellationToken token)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Append(string value)
            {
                token.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
            }
            Append(selection.AllMatching ? "all" : "explicit");
            Append(selection.Baseline?.DatabasePath ?? "");
            Append(selection.Keys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var key in selection.Keys) Append(PathKey(key));
            Append(selection.UnavailableKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var key in selection.UnavailableKeys) Append(PathKey(key));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
