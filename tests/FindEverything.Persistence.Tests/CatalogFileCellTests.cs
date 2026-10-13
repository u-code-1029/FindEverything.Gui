using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class CatalogFileCellTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fe-file-cell-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "results.db");
    public CatalogFileCellTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task File_provenance_roundtrips_and_single_cell_change_fences_old_queries()
    {
        using var store = new SqliteCatalogProjectionStore(Database);
        var snapshot = Seed(store);
        await using var old = store.OpenQuery(snapshot, new());
        var original = Assert.Single((await old.ReadRangeAsync(0, 500)).Items);
        Assert.Equal(ProfileFieldResourceState.Missing, original.Resources["note"].State);
        Assert.Contains("note", original.FieldErrors!.Keys);
        var saved = new ProfileFieldResourceInfo(ProfileFieldResourceState.Present, "note.txt", "utf-8", false, "\r\n", "ABC");
        var updated = await store.UpdateTextCellAsync(snapshot, original.FullPath, "note", "new content", saved);
        Assert.Equal(snapshot.Generation, updated.Generation);
        Assert.Equal(snapshot.Revision + 1, updated.Revision);
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => old.CountAsync());
        await using var current = store.OpenQuery(updated, new("new content"));
        Assert.Equal(1, await current.CountAsync());
        var row = Assert.Single((await current.ReadRangeAsync(0, 500)).Items);
        Assert.Equal("unchanged", row.Values["id"]);
        Assert.Equal("new content", row.Values["note"]);
        Assert.Equal(saved, row.Resources["note"]);
        Assert.Empty(row.FieldErrors!);
        Assert.Equal(0, updated.Summary.FieldIssueRowCount);
        Assert.Equal(updated.Revision, (await store.FindAsync(snapshot.Scope))!.Revision);
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => store.UpdateTextCellAsync(snapshot, row.FullPath, "note", "stale", saved));
    }

    [Fact]
    public async Task Column_refresh_recounts_rows_with_field_issues()
    {
        using var store = new SqliteCatalogProjectionStore(Database);
        var snapshot = Seed(store);
        Assert.Equal(1, snapshot.Summary.FieldIssueRowCount);
        var refreshed = await store.RefreshColumnAsync(snapshot, "note", (_, _) =>
            Task.FromResult(new TextColumnReadResult("recovered", [])));
        Assert.Equal(0, refreshed.Summary.FieldIssueRowCount);
        Assert.Equal(0, (await store.FindAsync(snapshot.Scope))!.Summary.FieldIssueRowCount);
    }

    [Fact]
    public async Task Cancelled_edit_and_invalid_field_leave_publication_untouched()
    {
        using var store = new SqliteCatalogProjectionStore(Database);
        var snapshot = Seed(store);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var resource = new ProfileFieldResourceInfo(ProfileFieldResourceState.Present, "note.txt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateTextCellAsync(snapshot, _directory, "note", "x", resource, cancel.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateTextCellAsync(snapshot, _directory, "id", "x", resource));
        Assert.Equal(0, (await store.FindAsync(snapshot.Scope))!.Revision);
        await using var query = store.OpenQuery(snapshot, new());
        Assert.Null(Assert.Single((await query.ReadRangeAsync(0, 500)).Items).Values["note"]);
    }

    [Fact]
    public async Task Version_two_cache_upgrade_preserves_values_without_filesystem_reads()
    {
        CatalogProjectionScope scope;
        using (var store = new SqliteCatalogProjectionStore(Database)) scope = Seed(store).Scope;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE projection_rows DROP COLUMN resources_json; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new SqliteCatalogProjectionStore(Database);
        var snapshot = (await upgraded.FindAsync(scope))!;
        await using var query = upgraded.OpenQuery(snapshot, new());
        var row = Assert.Single((await query.ReadRangeAsync(0, 500)).Items);
        Assert.Equal("unchanged", row.Values["id"]);
        Assert.Empty(row.Resources);
    }

    private CatalogProjectionSnapshot Seed(SqliteCatalogProjectionStore store)
    {
        var scope = new CatalogProjectionScope("scope", "profile", _directory, Database, "fingerprint", "en-US", Guid.NewGuid());
        ProfileFieldDescriptor[] fields = [
            new("id", "id", "ID", 0, true, ProfileFieldValueKind.String, false, null, null),
            new("note", "", "Note", 1, true, ProfileFieldValueKind.String, true, null, null) { SourceKind = ProfileFieldSourceKind.TextFileContent }
        ];
        using var writer = store.Begin(scope, fields);
        writer.Append(new(_directory, "", "rule", new object(), new Dictionary<string, object?> { ["id"] = "unchanged", ["note"] = null }, false)
        {
            Resources = new Dictionary<string, ProfileFieldResourceInfo> { ["note"] = new(ProfileFieldResourceState.Missing) },
            FieldErrors = new Dictionary<string, string> { ["note"] = "Required file missing" }
        });
        return writer.Publish(new(1, 1, 0, 0, 0, false, null, DateTimeOffset.UtcNow) { FieldIssueRowCount = 1 });
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
