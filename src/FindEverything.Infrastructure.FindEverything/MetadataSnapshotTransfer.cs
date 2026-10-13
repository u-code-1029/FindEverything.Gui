using System.Globalization;
using FindEverything.Application.Sharing;
using FindEverything.Engine;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FindEverything.Infrastructure.FindEverything;

public sealed record MetadataSnapshotInspection(string RootPath, Guid? ScanId, long EntryCount, bool HasPendingScopes);

/// <summary>Recreates the known engine schema and copies only a published root; never executes schema from a received database.</summary>
public static class MetadataSnapshotTransfer
{
    public static MetadataSnapshotInspection Inspect(string path, string? root = null, bool requireSingleRoot = false,
        CancellationToken cancellationToken = default)
    {
        try { return InspectCore(path, root, requireSingleRoot, cancellationToken); }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    private static MetadataSnapshotInspection InspectCore(string path, string? root, bool requireSingleRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenRead(path);
        using var interrupt = new SnapshotReadCancellation(connection, cancellationToken);
        Validate(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM roots;";
        if (requireSingleRoot && Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("A package must contain exactly one metadata root.");
        command.CommandText = "SELECT root_path,last_scan_id,(SELECT COUNT(*) FROM entries e WHERE e.root_key=r.root_key),EXISTS(SELECT 1 FROM pending_scopes p WHERE p.root_key=r.root_key) FROM roots r "
            + (root is null ? "LIMIT 1;" : "WHERE root_key=$root;");
        if (root is not null) command.Parameters.AddWithValue("$root", PathRules.Key(root));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("The selected root has no published index.");
        var scan = reader.IsDBNull(1) ? (Guid?)null : Guid.ParseExact(reader.GetString(1), "N");
        if (scan is null) throw new InvalidDataException("The selected root has no published scan.");
        var result = new MetadataSnapshotInspection(reader.GetString(0), scan, reader.GetInt64(2), reader.GetInt64(3) != 0);
        reader.Close();
        if (requireSingleRoot)
        {
            foreach (var table in new[] { "scans", "staging", "staged_costs", "staged_pending" })
            {
                command.CommandText = "SELECT COUNT(*) FROM " + table;
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidDataException("A package contains unpublished engine staging data.");
            }
        }
        return result;
    }

    public static async Task<MetadataSnapshotInspection> CopyAsync(string source, string storedRoot, string destination,
        string effectiveRoot, IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try { return await CopyCoreAsync(source, storedRoot, destination, effectiveRoot, progress, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    private static async Task<MetadataSnapshotInspection> CopyCoreAsync(string source, string storedRoot, string destination,
        string effectiveRoot, IProgress<WorkspaceTransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) throw new IOException("The metadata destination already exists.");
        effectiveRoot = WorkspacePathMapping.NormalizeRoot(effectiveRoot);
        var info = Inspect(source, storedRoot, cancellationToken: cancellationToken);
        await using (var store = new SqliteIndexStore(destination))
        {
            await using var lease = await store.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        using var input = OpenRead(source);
        using var interrupt = new SnapshotReadCancellation(input, cancellationToken);
        Validate(input);
        using var readTransaction = input.BeginTransaction(deferred: true);
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        output.Open();
        using var transaction = output.BeginTransaction();
        using var read = input.CreateCommand();
        read.Transaction = readTransaction;
        read.Parameters.AddWithValue("$root", PathRules.Key(storedRoot));
        read.CommandText = "SELECT last_scan_id,last_scope_path,last_status,last_published_ticks,last_entries,last_errors FROM roots WHERE root_key=$root;";
        using var write = output.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "INSERT INTO roots(root_key,root_path,last_scan_id,last_scope_path,last_status,last_published_ticks,last_entries,last_errors) VALUES($key,$path,$scan,$scope,$status,$ticks,$entries,$errors);";
        write.Parameters.AddWithValue("$key", PathRules.Key(effectiveRoot));
        write.Parameters.AddWithValue("$path", effectiveRoot);
        using (var rootReader = read.ExecuteReader())
        {
            if (!rootReader.Read()) throw new InvalidDataException("The metadata root disappeared.");
            var scan = rootReader.IsDBNull(0) ? null : rootReader.GetString(0);
            if (scan != info.ScanId?.ToString("N")) throw new InvalidDataException("The metadata publication changed during export. Retry after scanning completes.");
            write.Parameters.AddWithValue("$scan", (object?)scan ?? DBNull.Value);
            write.Parameters.AddWithValue("$scope", rootReader.IsDBNull(1) ? DBNull.Value
                : WorkspacePathMapping.Join(effectiveRoot, WorkspacePathMapping.GetRelative(storedRoot, rootReader.GetString(1))));
            foreach (var (name, index) in new[] { ("$status", 2), ("$ticks", 3), ("$entries", 4), ("$errors", 5) })
                write.Parameters.AddWithValue(name, rootReader.GetValue(index));
            write.ExecuteNonQuery();
        }
        write.Parameters.Clear();
        write.CommandText = "INSERT INTO entries(root_key,path_key,full_path,path_search,name,name_search,file_type,parent_path,kind,size_bytes,created_ticks,modified_ticks) VALUES($root,$key,$path,$search,$name,$nameSearch,$type,$parent,$kind,$size,$created,$modified);";
        write.Parameters.AddWithValue("$root", PathRules.Key(effectiveRoot));
        foreach (var name in new[] { "$key", "$path", "$search", "$name", "$nameSearch", "$type", "$parent", "$kind", "$size", "$created", "$modified" })
            write.Parameters.AddWithValue(name, DBNull.Value);
        write.Prepare();
        read.CommandText = "SELECT full_path,name,name_search,file_type,parent_path,kind,size_bytes,created_ticks,modified_ticks FROM entries WHERE root_key=$root;";
        var completed = 0L;
        using (var rows = read.ExecuteReader())
        {
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = WorkspacePathMapping.Join(effectiveRoot, WorkspacePathMapping.GetRelative(storedRoot, rows.GetString(0)));
                write.Parameters["$key"].Value = PathRules.Key(path);
                write.Parameters["$path"].Value = path;
                write.Parameters["$search"].Value = path.ToLowerInvariant();
                foreach (var (name, index) in new[] { ("$name", 1), ("$nameSearch", 2), ("$type", 3), ("$kind", 5), ("$size", 6), ("$created", 7), ("$modified", 8) })
                    write.Parameters[name].Value = rows.GetValue(index);
                var relative = WorkspacePathMapping.GetRelative(storedRoot, rows.GetString(0));
                write.Parameters["$parent"].Value = relative.Length == 0 ? Path.GetDirectoryName(effectiveRoot) ?? string.Empty
                    : WorkspacePathMapping.Join(effectiveRoot, Path.GetDirectoryName(relative) ?? string.Empty);
                write.ExecuteNonQuery();
                if (++completed % 500 == 0) progress?.Report(new("metadata", completed, info.EntryCount));
            }
        }
        CopyScopes(input, readTransaction, output, transaction, storedRoot, effectiveRoot, "directory_costs", cancellationToken);
        CopyScopes(input, readTransaction, output, transaction, storedRoot, effectiveRoot, "pending_scopes", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        readTransaction.Commit();
        write.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        write.Transaction = null;
        write.Parameters.Clear();
        write.ExecuteNonQuery();
        progress?.Report(new("metadata", completed, completed));
        return info with { RootPath = effectiveRoot, EntryCount = completed };
    }

    private static void CopyScopes(SqliteConnection input, SqliteTransaction readTransaction, SqliteConnection output,
        SqliteTransaction transaction, string original, string linked, string table, CancellationToken ct)
    {
        var costs = table == "directory_costs";
        using var read = input.CreateCommand();
        read.Transaction = readTransaction;
        read.CommandText = costs ? "SELECT full_path,entries,duration_ticks,recorded_ticks FROM directory_costs WHERE root_key=$root;"
            : "SELECT scope_path,reason,estimated_entries,estimated_duration_ticks,deferred_ticks FROM pending_scopes WHERE root_key=$root;";
        read.Parameters.AddWithValue("$root", PathRules.Key(original));
        using var rows = read.ExecuteReader();
        using var write = output.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = costs ? "INSERT INTO directory_costs(root_key,path_key,full_path,entries,duration_ticks,recorded_ticks) VALUES($root,$key,$path,$v1,$v2,$v3);"
            : "INSERT INTO pending_scopes(root_key,path_key,scope_path,lower_bound,upper_bound,reason,estimated_entries,estimated_duration_ticks,deferred_ticks) VALUES($root,$key,$path,$lower,$upper,$v1,$v2,$v3,$v4);";
        write.Parameters.AddWithValue("$root", PathRules.Key(linked));
        foreach (var name in costs ? new[] { "$key", "$path", "$v1", "$v2", "$v3" } : new[] { "$key", "$path", "$lower", "$upper", "$v1", "$v2", "$v3", "$v4" })
            write.Parameters.AddWithValue(name, DBNull.Value);
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var path = WorkspacePathMapping.Join(linked, WorkspacePathMapping.GetRelative(original, rows.GetString(0)));
            var key = PathRules.Key(path);
            write.Parameters["$key"].Value = key;
            write.Parameters["$path"].Value = path;
            for (var i = 1; i < rows.FieldCount; i++) write.Parameters["$v" + i].Value = rows.GetValue(i);
            if (!costs)
            {
                var prefix = Path.EndsInDirectorySeparator(key) ? key : key + Path.DirectorySeparatorChar;
                write.Parameters["$lower"].Value = prefix;
                write.Parameters["$upper"].Value = prefix[..^1] + (char)(prefix[^1] + 1);
            }
            write.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Validate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0x46455631)
            throw new InvalidDataException("The package metadata has an invalid application ID.");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 4)
            throw new InvalidDataException("The metadata schema is not supported.");
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("The metadata database is corrupt.");
        command.CommandText = "PRAGMA foreign_key_check;";
        using var errors = command.ExecuteReader();
        if (errors.Read()) throw new InvalidDataException("The metadata has invalid foreign keys.");
    }

    private sealed class SnapshotReadCancellation : IDisposable
    {
        private readonly sqlite3 _handle;
        private readonly delegate_progress _progress;
        private readonly CancellationTokenRegistration _registration;
        private bool _reference;
        public SnapshotReadCancellation(SqliteConnection connection, CancellationToken token)
        {
            _handle = connection.Handle!;
            _handle.DangerousAddRef(ref _reference);
            try
            {
                _progress = _ => token.IsCancellationRequested ? 1 : 0;
                raw.sqlite3_progress_handler(_handle, 1000, _progress, null!);
                _registration = token.Register(static state => raw.sqlite3_interrupt((sqlite3)state!), _handle);
            }
            catch { if (_reference) _handle.DangerousRelease(); throw; }
        }
        public void Dispose()
        {
            _registration.Dispose();
            raw.sqlite3_progress_handler(_handle, 0, null!, null!);
            if (_reference) { _handle.DangerousRelease(); _reference = false; }
            GC.KeepAlive(_progress);
        }
    }
}
