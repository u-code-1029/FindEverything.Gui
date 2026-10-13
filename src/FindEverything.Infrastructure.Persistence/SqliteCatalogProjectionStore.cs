using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>Published projections and user edits are independent from engine metadata.</summary>
public sealed partial class SqliteCatalogProjectionStore : ICatalogProjectionStore, IUserInputStore, IDisposable
{
    private const int ApplicationId = 0x46454750;
    private const int BatchSize = 500;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> DatabaseLeases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _leases;
    private readonly string _connectionString;
    internal bool HasResourceColumn { get; }
    private bool _disposed;

    public SqliteCatalogProjectionStore(string databasePath) : this(databasePath, false) { }

    public SqliteCatalogProjectionStore(string databasePath, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _leases = DatabaseLeases.GetOrAdd(PathKey(DatabasePath), static _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        if (OperatingSystem.IsWindows() && (DatabasePath.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(DatabasePath)!).DriveType == DriveType.Network))
            throw new ArgumentException("The catalog database must be on a local disk.", nameof(databasePath));
        var exists = File.Exists(DatabasePath);
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 30
        }.ToString();
        using var connection = OpenConnection();
        using var ownership = connection.CreateCommand();
        ownership.CommandText = "PRAGMA application_id;";
        if (exists && Convert.ToInt64(ownership.ExecuteScalar(), CultureInfo.InvariantCulture) != ApplicationId)
            throw new InvalidDataException("The catalog path contains a different database.");
        ownership.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(ownership.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is < 0 or > 3)
            throw new InvalidDataException($"Unsupported catalog schema version {version}.");
        if (readOnly)
        {
            if (!exists || version is not (2 or 3)) throw new InvalidDataException("A read-only catalog requires a supported published schema.");
            HasResourceColumn = HasColumn(connection, "projection_rows", "resources_json");
            if (version == 3 && !HasResourceColumn) throw new InvalidDataException("The catalog resource schema is incomplete.");
            return;
        }
        if (version == 1) UpgradeVersionOne(connection);
        if (version is 1 or 2) UpgradeResources(connection);
        using var schema = connection.CreateCommand();
        schema.CommandText = Schema;
        schema.ExecuteNonQuery();
        HasResourceColumn = true;
        // Another app instance can be building a generation. Its staging data is
        // never deleted simply because a second instance opens this database.
    }

    public string DatabasePath { get; }

    public Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope,
        CancellationToken cancellationToken = default) => Task.Run(() => SqliteReadCancellation.RunAsync(
            token => FindCoreAsync(scope, token), cancellationToken), cancellationToken);

    private async Task<CatalogProjectionSnapshot?> FindCoreAsync(CatalogProjectionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var connection = OpenConnection(readOperation: true);
        using var interrupt = new SqliteReadCancellation(connection, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.generation, g.scope_json, g.summary_json, g.fields_json, g.revision
            FROM projection_publications p JOIN projection_generations g ON g.generation=p.generation
            WHERE p.scope_key=$scope;
            """;
        command.Parameters.AddWithValue("$scope", scope.Key);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var savedScope = Deserialize<CatalogProjectionScope>(reader.GetString(1));
        if (!Equals(savedScope, scope)) return null;
        return new CatalogProjectionSnapshot(reader.GetString(0), savedScope,
            Deserialize<CatalogProjectionSummary>(reader.GetString(2)),
            Deserialize<ProfileFieldDescriptor[]>(reader.GetString(3))) { Revision = reader.GetInt64(4) };
    }

    public ICatalogProjectionWriter Begin(CatalogProjectionScope scope,
        IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fields);
        var generation = Guid.NewGuid().ToString("N");
        _writeGate.Wait();
        try
        {
            using var connection = OpenConnection();
            var previous = CurrentGeneration(connection, null, scope.Key);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projection_generations(generation, scope_key, logical_key, scope_json, fields_json, state, row_count)
                VALUES($generation, $scope, $logical, $scopeJson, $fields, 0, 0);
                """;
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$scope", scope.Key);
            command.Parameters.AddWithValue("$logical", LogicalScopeKey(scope));
            command.Parameters.AddWithValue("$scopeJson", JsonSerializer.Serialize(scope));
            command.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(fields));
            command.ExecuteNonQuery();
            Prune(connection);
            return new ProjectionWriter(this, generation, scope, fields.ToArray(), previous);
        }
        finally { _writeGate.Release(); }
    }

    public IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ProjectionQuery(this, snapshot.Generation, snapshot.Scope, snapshot.Fields, query,
            snapshot.IsBuilding, snapshot.IsBuilding ? snapshot.BuildingRevision : snapshot.Revision);
    }

    public IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer,
        CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields,
        CatalogProjectionQuery query)
    {
        if (writer is not ProjectionWriter concrete || !ReferenceEquals(concrete.Owner, this))
            throw new ArgumentException("The writer belongs to another store.", nameof(writer));
        concrete.Flush();
        return new ProjectionQuery(this, writer.Generation, scope, fields, query, true);
    }

    public IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT field_id, value FROM user_inputs WHERE profile_key=$profile AND path_key=$path;";
        command.Parameters.AddWithValue("$profile", ProfileKey(profileId));
        command.Parameters.AddWithValue("$path", PathKey(absolutePath));
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    public async Task SetValueAsync(string profileId, string absolutePath, string fieldId,
        string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        var fieldKey = fieldId.ToLowerInvariant();
        if (!IsStableKey(fieldKey, allowUnderscore: true))
            throw new ArgumentException("The field ID must match the profile field contract.", nameof(fieldId));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > ProfileManifestLimits.MaximumUserInputValueLength)
            throw new ArgumentOutOfRangeException(nameof(value), "The user input value is too long.");
        var profileKey = ProfileKey(profileId);
        var pathKey = PathKey(absolutePath);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO user_inputs(profile_key, path_key, field_id, value, value_search)
                VALUES($profile, $path, $field, $value, $search)
                ON CONFLICT(profile_key, path_key, field_id) DO UPDATE SET value=excluded.value, value_search=excluded.value_search
                WHERE user_inputs.value IS NOT excluded.value;
                """;
            command.Parameters.AddWithValue("$profile", profileKey);
            command.Parameters.AddWithValue("$path", pathKey);
            command.Parameters.AddWithValue("$field", fieldKey);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$search", value.ToLowerInvariant());
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO user_input_revisions(profile_key, revision) VALUES($profile, 1)
                    ON CONFLICT(profile_key) DO UPDATE SET revision=revision+1;
                    """;
                command.Parameters.AddWithValue("$profile", profileKey);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { _writeGate.Release(); }
    }

    public async Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot,
        string fieldId, Func<string, CancellationToken, Task<TextColumnReadResult>> readValue,
        IProgress<TextColumnRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(readValue);
        var field = snapshot.Fields.FirstOrDefault(item => string.Equals(item.FieldId, fieldId, StringComparison.OrdinalIgnoreCase));
        if (field?.SourceKind != ProfileFieldSourceKind.TextFileContent)
            throw new ArgumentException("Only text-file columns can be refreshed.", nameof(fieldId));
        using var writer = (ProjectionWriter)Begin(snapshot.Scope, snapshot.Fields);
        await using var source = (ProjectionQuery)OpenQuery(snapshot, new CatalogProjectionQuery());
        var total = checked((int)await source.CountAsync(cancellationToken).ConfigureAwait(false));
        var completed = 0;
        var errors = 0;
        var fieldIssueRows = 0;
        await foreach (var row in source.EnumerateSelectionAsync(new QuerySelection(true), cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await readValue(row.FullPath, cancellationToken).ConfigureAwait(false);
            var values = row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var fieldErrors = new Dictionary<string, string>(row.FieldErrors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            var resources = row.Resources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (result.Resource is not null) resources[field.FieldId] = result.Resource;
            if (result.Issues.Count == 0)
            {
                values[field.FieldId] = result.Value;
                fieldErrors.Remove(field.FieldId);
            }
            else
            {
                errors++;
                fieldErrors[field.FieldId] = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
            }
            if (fieldErrors.Count > 0) fieldIssueRows++;
            writer.AppendRow(row with { Values = values, FieldErrors = fieldErrors, Resources = resources });
            progress?.Report(new TextColumnRefreshProgress(++completed, total, errors));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return writer.Publish(snapshot.Summary with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            FieldIssueRowCount = fieldIssueRows,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private SqliteConnection OpenConnection(bool readOperation = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        if (readOperation) connection.DefaultTimeout = 1;
        try
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            setup.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static string? CurrentGeneration(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation FROM projection_publications WHERE scope_key=$scope;";
        command.Parameters.AddWithValue("$scope", key);
        return command.ExecuteScalar() as string;
    }

    private static long NotesRevision(SqliteConnection connection, SqliteTransaction transaction, string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM user_input_revisions WHERE profile_key=$profile;";
        command.Parameters.AddWithValue("$profile", ProfileKey(profileId));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Prune(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation FROM projection_generations g WHERE state=1 AND NOT EXISTS(SELECT 1 FROM projection_publications p WHERE p.generation=g.generation);";
        var obsolete = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (!_leases.ContainsKey(reader.GetString(0))) obsolete.Add(reader.GetString(0));
        foreach (var generation in obsolete)
        {
            command.CommandText = "DELETE FROM projection_generations WHERE generation=$generation;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$generation", generation);
            command.ExecuteNonQuery();
        }
    }

    private static void UpgradeVersionOne(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(projection_generations);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("revision"))
        {
            command.CommandText = "ALTER TABLE projection_generations ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("logical_key"))
        {
            command.CommandText = "ALTER TABLE projection_generations ADD COLUMN logical_key TEXT NOT NULL DEFAULT '';";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT generation, scope_json FROM projection_generations;";
            var keys = new List<(string Generation, string Key)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) keys.Add((reader.GetString(0), LogicalScopeKey(Deserialize<CatalogProjectionScope>(reader.GetString(1)))));
            command.CommandText = "UPDATE projection_generations SET logical_key=$key WHERE generation=$generation;";
            var key = command.Parameters.Add("$key", SqliteType.Text);
            var generation = command.Parameters.Add("$generation", SqliteType.Text);
            foreach (var item in keys)
            {
                key.Value = item.Key;
                generation.Value = item.Generation;
                command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();
        command.CommandText = "PRAGMA user_version=2;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1) == column) return true;
        return false;
    }

    private static void UpgradeResources(SqliteConnection connection)
    {
        var exists = HasColumn(connection, "projection_rows", "resources_json");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (!exists)
        {
            command.CommandText = "ALTER TABLE projection_rows ADD COLUMN resources_json TEXT NOT NULL DEFAULT '{}';";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version=3;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException("The catalog contains invalid serialized data.");
    private static string LogicalScopeKey(CatalogProjectionScope scope) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[]
        { ProfileKey(scope.ProfileId), PathKey(scope.RootPath), PathKey(scope.DatabasePath), scope.CultureName.ToLowerInvariant() })));
    private static string ProfileKey(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var key = profileId.ToLowerInvariant();
        if (!IsStableKey(key, allowUnderscore: false))
            throw new ArgumentException("The profile ID must match the profile contract.", nameof(profileId));
        return key;
    }
    private static bool IsStableKey(string key, bool allowUnderscore) => key.Length is >= 1 and <= 64
        && (key[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
        && key.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'
            || allowUnderscore && character == '_');
    private static string PathKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    }

    private const string Schema = """
        PRAGMA application_id=1178945360;
        PRAGMA user_version=3;
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS projection_generations(
            generation TEXT PRIMARY KEY, scope_key TEXT NOT NULL, logical_key TEXT NOT NULL,
            scope_json TEXT NOT NULL, fields_json TEXT NOT NULL, summary_json TEXT,
            state INTEGER NOT NULL, row_count INTEGER NOT NULL, revision INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS generations_logical_key ON projection_generations(logical_key);
        CREATE TABLE IF NOT EXISTS projection_publications(
            scope_key TEXT PRIMARY KEY, generation TEXT NOT NULL REFERENCES projection_generations(generation));
        CREATE TABLE IF NOT EXISTS projection_rows(
            generation TEXT NOT NULL REFERENCES projection_generations(generation) ON DELETE CASCADE,
            path_key TEXT NOT NULL COLLATE BINARY, full_path TEXT NOT NULL,
            path_search TEXT NOT NULL, relative_path TEXT NOT NULL, rule_id TEXT NOT NULL,
            values_json TEXT NOT NULL, errors_json TEXT NOT NULL, coverage INTEGER NOT NULL,
            resources_json TEXT NOT NULL DEFAULT '{}',
            PRIMARY KEY(generation, path_key)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS projection_cells(
            generation TEXT NOT NULL, path_key TEXT NOT NULL, field_id TEXT NOT NULL,
            raw_search TEXT NOT NULL, mapped_search TEXT NOT NULL,
            sort_numeric TEXT, sort_ticks INTEGER, raw_sort TEXT, mapped_sort TEXT,
            is_user_input INTEGER NOT NULL,
            PRIMARY KEY(generation, path_key, field_id),
            FOREIGN KEY(generation, path_key) REFERENCES projection_rows(generation, path_key) ON DELETE CASCADE) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS cells_field_raw ON projection_cells(generation, field_id, raw_sort, path_key);
        CREATE INDEX IF NOT EXISTS cells_field_mapped ON projection_cells(generation, field_id, mapped_sort, path_key);
        CREATE INDEX IF NOT EXISTS cells_field_number ON projection_cells(generation, field_id, sort_numeric, path_key);
        CREATE INDEX IF NOT EXISTS cells_field_ticks ON projection_cells(generation, field_id, sort_ticks, path_key);
        CREATE TABLE IF NOT EXISTS user_inputs(
            profile_key TEXT NOT NULL, path_key TEXT NOT NULL COLLATE BINARY,
            field_id TEXT NOT NULL, value TEXT NOT NULL, value_search TEXT NOT NULL,
            PRIMARY KEY(profile_key, path_key, field_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS user_input_revisions(
            profile_key TEXT PRIMARY KEY, revision INTEGER NOT NULL);
        """;
}
