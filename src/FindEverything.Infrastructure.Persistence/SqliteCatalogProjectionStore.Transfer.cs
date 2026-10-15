using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

public sealed partial class SqliteCatalogProjectionStore
{
    /// <summary>Copies one leased published generation into a newly initialized database, never the shared results.db.</summary>
    public async Task<CatalogProjectionSnapshot> ExportSnapshotAsync(CatalogProjectionSnapshot snapshot,
        string destination, bool includeUserInputs, string originalRoot, string effectiveRoot,
        string profileId, string metadataDatabasePath, string cultureName,
        IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (snapshot.IsBuilding) throw new InvalidDataException("Only published structured results can be exported.");
        if (File.Exists(destination)) throw new IOException("The snapshot destination already exists.");
        originalRoot = WorkspacePathMapping.NormalizeRoot(originalRoot);
        effectiveRoot = WorkspacePathMapping.NormalizeRoot(effectiveRoot);
        using var target = new SqliteCatalogProjectionStore(destination);
        var scope = snapshot.Scope with
        {
            Key = "import-" + Guid.NewGuid().ToString("N"), ProfileId = profileId,
            RootPath = effectiveRoot, DatabasePath = metadataDatabasePath, CultureName = cultureName,
        };
        using var writer = (ProjectionWriter)target.Begin(scope, snapshot.Fields);
        await using var query = OpenQuery(snapshot, new CatalogProjectionQuery());
        var inputFields = snapshot.Fields.Where(f => f.SourceKind == ProfileFieldSourceKind.UserInput).ToArray();
        var notes = new List<(string Path, string Field, string Value)>();
        var completed = 0L;
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true), cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative;
            try { relative = WorkspacePathMapping.GetRelative(snapshot.Scope.RootPath, row.FullPath); }
            catch (InvalidDataException) { relative = WorkspacePathMapping.GetRelative(originalRoot, row.FullPath); }
            // The scope root can be a mapped drive while the parser received canonical UNC paths.
            if (!string.Equals(WorkspacePathMapping.ValidateRelative(row.RelativePath), relative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A structured row has an inconsistent relative path.");
            var effectivePath = WorkspacePathMapping.Join(effectiveRoot, relative);
            var values = row.Values.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var field in inputFields)
            {
                if (includeUserInputs && values.TryGetValue(field.FieldId, out var value) && value is string text)
                    notes.Add((effectivePath, field.FieldId, text));
                if (!includeUserInputs) values[field.FieldId] = field.DefaultValue ?? string.Empty;
            }
            writer.AppendRow(row with { FullPath = effectivePath, RelativePath = relative, Values = values,
                StableKey = null, OriginalFullPath = null });
            completed++;
            if (completed % BatchSize == 0)
            {
                writer.Flush();
                target.WriteTransferredNotes(profileId, notes, cancellationToken);
                notes.Clear();
                progress?.Report(new("catalog", completed, snapshot.Summary.MatchedCount));
            }
        }
        writer.Flush();
        target.WriteTransferredNotes(profileId, notes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var published = writer.Publish(snapshot.Summary);
        using var connection = target.OpenConnection();
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        checkpoint.ExecuteNonQuery();
        progress?.Report(new("catalog", completed, completed));
        return published;
    }

    public static CatalogProjectionSnapshot ValidateTransferredSnapshot(string path, CancellationToken cancellationToken = default)
    {
        try { return ValidateTransferredSnapshotCore(path, cancellationToken); }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    private static CatalogProjectionSnapshot ValidateTransferredSnapshotCore(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var interrupt = new SqliteReadCancellation(connection, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA application_id;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != ApplicationId)
            throw new InvalidDataException("The package catalog database has an invalid application ID.");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) is not (2 or 3))
            throw new InvalidDataException("The package catalog schema is not supported.");
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("The catalog database is corrupt.");
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var violations = command.ExecuteReader())
            if (violations.Read()) throw new InvalidDataException("The package catalog has invalid foreign keys.");
        command.CommandText = "SELECT COUNT(*) FROM projection_generations;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("A package must contain exactly one structured generation.");
        command.CommandText = """
            SELECT g.generation,g.scope_json,g.summary_json,g.fields_json,g.row_count,
                (SELECT COUNT(*) FROM projection_rows r WHERE r.generation=g.generation)
                ,g.revision
            FROM projection_publications p JOIN projection_generations g ON p.generation=g.generation
            WHERE g.state=1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(4) != reader.GetInt64(5))
            throw new InvalidDataException("The published catalog row count is invalid.");
        var snapshot = new CatalogProjectionSnapshot(reader.GetString(0), Deserialize<CatalogProjectionScope>(reader.GetString(1)),
            Deserialize<CatalogProjectionSummary>(reader.GetString(2)), Deserialize<ProfileFieldDescriptor[]>(reader.GetString(3)))
            { Revision = reader.GetInt64(6) };
        if (snapshot.Summary.MatchedCount != reader.GetInt64(5)) throw new InvalidDataException("The structured summary does not match its rows.");
        if (reader.Read()) throw new InvalidDataException("A package contains multiple catalog publications.");
        return snapshot;
    }

    private void WriteTransferredNotes(string profileId, IReadOnlyList<(string Path, string Field, string Value)> notes, CancellationToken ct)
    {
        if (notes.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO user_inputs(profile_key,path_key,field_id,value,value_search) VALUES($profile,$path,$field,$value,$search);";
        command.Parameters.AddWithValue("$profile", ProfileKey(profileId));
        foreach (var parameter in new[] { "$path", "$field", "$value", "$search" }) command.Parameters.Add(parameter, SqliteType.Text);
        command.Prepare();
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            command.Parameters["$path"].Value = PathKey(note.Path);
            command.Parameters["$field"].Value = note.Field.ToLowerInvariant();
            command.Parameters["$value"].Value = note.Value;
            command.Parameters["$search"].Value = note.Value.ToLowerInvariant();
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}

/// <summary>Read-only facade. Returned paths are linked paths, while selection keys keep their original identity.</summary>
public sealed class ImportedCatalogProjectionStore : IImportedCatalogProjectionStore
{
    private readonly SqliteCatalogProjectionStore _inner;
    private readonly ImportedWorkspaceSource _source;
    public ImportedCatalogProjectionStore(ImportedWorkspaceSource source)
    {
        if (!source.HasCatalog) throw new InvalidOperationException("This package has no structured results.");
        _source = source;
        _inner = new(source.CatalogDatabasePath!, readOnly: true);
    }
    public Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope, CancellationToken cancellationToken = default)
        => Task.FromResult<CatalogProjectionSnapshot?>(_source.CatalogSnapshot);
    public IResultQuerySession<CatalogProjectionRow> OpenImportedQuery(CatalogProjectionQuery query)
        => new ImportedQuery(_inner.OpenQuery(_source.CatalogSnapshot!, query), _source);
    public IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot, CatalogProjectionQuery query)
        => OpenImportedQuery(query);
    public Task<CatalogNumericRange?> ReadNumericRangeAsync(CatalogProjectionSnapshot snapshot, string fieldId,
        CancellationToken cancellationToken = default) => _inner.ReadNumericRangeAsync(_source.CatalogSnapshot!, fieldId, cancellationToken);
    public ICatalogProjectionWriter Begin(CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields) => throw ReadOnly();
    public IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer, CatalogProjectionScope scope,
        IReadOnlyList<ProfileFieldDescriptor> fields, CatalogProjectionQuery query) => throw ReadOnly();
    public Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot, string fieldId,
        Func<string, CancellationToken, Task<TextColumnReadResult>> readValue, IProgress<TextColumnRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default) => throw ReadOnly();
    public void Dispose() => _inner.Dispose();
    private static InvalidOperationException ReadOnly() => new("Imported workspace data is read-only. Start an explicit local scan to create a new result.");

    private sealed class ImportedQuery(IResultQuerySession<CatalogProjectionRow> inner, ImportedWorkspaceSource source)
        : IResultQuerySession<CatalogProjectionRow>, ICatalogSelectionMatchQuery, IResultSelectionKeyReader
    {
        public string Identity => source.Id + ":" + source.LinkRevision + ":" + inner.Identity;
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => inner.CountAsync(cancellationToken);
        public async Task<long> CountSelectionAsync(QuerySelection selection, CancellationToken cancellationToken = default)
        {
            using var mapped = selection.MapKeys(key => WorkspacePathMapping.ToEffective(source, key));
            return await inner.CountSelectionAsync(mapped, cancellationToken).ConfigureAwait(false);
        }
        public async IAsyncEnumerable<ResultSelectionKey> EnumerateSelectionKeysAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var mapped = selection.MapKeys(key => WorkspacePathMapping.ToEffective(source, key));
            if (inner is IResultSelectionKeyReader reader)
                await foreach (var key in reader.EnumerateSelectionKeysAsync(mapped, cancellationToken).ConfigureAwait(false))
                    yield return new(WorkspacePathMapping.ToOriginal(source, key.QueryKey), key.QueryKey);
            else
                await foreach (var row in inner.EnumerateSelectionAsync(mapped, cancellationToken).ConfigureAwait(false))
                    yield return new(WorkspacePathMapping.ToOriginal(source, row.FullPath), row.FullPath);
        }
        public async Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default)
        {
            var page = await inner.ReadRangeAsync(offset, limit, cancellationToken).ConfigureAwait(false);
            return page with { Items = page.Items.Select(Map).ToArray() };
        }
        public async Task<ResultPage<CatalogProjectionRow>> ReadSelectionRangeAsync(QuerySelection selection,
            int offset, int limit, CancellationToken cancellationToken = default)
        {
            using var mapped = selection.MapKeys(key => WorkspacePathMapping.ToEffective(source, key));
            var page = await inner.ReadSelectionRangeAsync(mapped, offset, limit, cancellationToken).ConfigureAwait(false);
            return page with { Items = page.Items.Select(Map).ToArray() };
        }
        public async IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var mapped = selection.MapKeys(key => WorkspacePathMapping.ToEffective(source, key));
            await foreach (var row in inner.EnumerateSelectionAsync(mapped, cancellationToken).ConfigureAwait(false)) yield return Map(row);
        }
        public Task<CatalogSelectionMatchResult> CreateSelectionMatchAsync(CatalogSelectionMatchInput input,
            Func<string, string>? selectionKeyMapper = null, CancellationToken cancellationToken = default)
        {
            if (inner is not ICatalogSelectionMatchQuery matching)
                throw new NotSupportedException("This catalog provider does not support data-based selection.");
            return matching.CreateSelectionMatchAsync(input, path =>
            {
                var original = WorkspacePathMapping.ToOriginal(source, path);
                return selectionKeyMapper?.Invoke(original) ?? original;
            }, cancellationToken);
        }
        private CatalogProjectionRow Map(CatalogProjectionRow row)
        {
            var original = WorkspacePathMapping.ToOriginal(source, row.FullPath);
            return row with { StableKey = original, OriginalFullPath = original };
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
