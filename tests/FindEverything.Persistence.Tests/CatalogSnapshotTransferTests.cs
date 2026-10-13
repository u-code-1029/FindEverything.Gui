using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class CatalogSnapshotTransferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotCopiesOnlyOneGenerationAndOptedInNotesWithFullTypedValues(bool notes)
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "original");
        var linked = Path.Combine(directory.Path, "receiver");
        var database = Path.Combine(directory.Path, "results.db");
        using var store = new SqliteCatalogProjectionStore(database);
        var scope = Scope("profile", root, database);
        var fields = new[] { Field("amount", ProfileFieldValueKind.Decimal), Field("text", ProfileFieldValueKind.String),
            Field("memo", ProfileFieldValueKind.String) with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default" } };
        var content = new string('x', 5000) + "tail";
        var path = Path.Combine(root, "item");
        using var writer = store.Begin(scope, fields);
        writer.Append(Item(path, "item", new() { ["amount"] = 123456789.1234567890123456789m, ["text"] = content }));
        var snapshot = writer.Publish(Summary(1));
        await store.SetValueAsync("profile", path, "memo", "private note");
        using var unrelated = store.Begin(Scope("unrelated", Path.Combine(directory.Path, "secret"), database), fields);
        unrelated.Append(Item(Path.Combine(directory.Path, "secret", "private"), "private", new() { ["text"] = "OTHER ROOT SECRET" }));
        unrelated.Publish(Summary(1));

        var exported = Path.Combine(directory.Path, "catalog.db");
        var transferred = await store.ExportSnapshotAsync(snapshot, exported, notes, root, linked, "receiver-profile",
            Path.Combine(directory.Path, "metadata.db"), "en-US");
        var validated = SqliteCatalogProjectionStore.ValidateTransferredSnapshot(exported);
        Assert.Equal(transferred.Generation, validated.Generation);
        using var imported = new SqliteCatalogProjectionStore(exported, readOnly: true);
        await using var query = imported.OpenQuery(transferred, new());
        var row = Assert.Single((await query.ReadRangeAsync(0, 20)).Items);
        Assert.Equal(Path.Combine(linked, "item"), row.FullPath);
        Assert.Equal(content, row.Values["text"]);
        Assert.Equal(123456789.1234567890123456789m, row.Values["amount"]);
        Assert.Equal(notes ? "private note" : "default", row.Values["memo"]);
        using var connection = new SqliteConnection($"Data Source={exported};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM projection_generations;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM projection_rows WHERE values_json LIKE '%OTHER ROOT SECRET%';";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM user_inputs;";
        Assert.Equal(notes ? 1L : 0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ImportedQueryKeepsOriginalKeysForExplicitAndInvertedSelections()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "original");
        var linked = Path.Combine(directory.Path, "linked");
        var database = Path.Combine(directory.Path, "source.db");
        using var store = new SqliteCatalogProjectionStore(database);
        using var writer = store.Begin(Scope("profile", root, database), [Field("text", ProfileFieldValueKind.String)]);
        foreach (var name in new[] { "a", "b" }) writer.Append(Item(Path.Combine(root, name), name, new() { ["text"] = name }));
        var snapshot = writer.Publish(Summary(2));
        var target = Path.Combine(directory.Path, "target.db");
        var transferred = await store.ExportSnapshotAsync(snapshot, target, false, root, linked, "profile",
            Path.Combine(directory.Path, "metadata.db"), "en-US");
        var source = new ImportedWorkspaceSource("id", "profile", "imported", root, linked, directory.Path,
            "metadata.db", target, transferred, 2, false, false, DateTimeOffset.UtcNow, 0,
            new ProfileManifest { Kind = ProfileKind.Declarative });
        using var facade = new ImportedCatalogProjectionStore(source);
        Assert.Throws<InvalidOperationException>(() => facade.Begin(transferred.Scope, transferred.Fields));
        await using var query = facade.OpenImportedQuery(new());
        var page = await query.ReadRangeAsync(0, 20);
        Assert.All(page.Items, row => Assert.Equal(WorkspacePathMapping.ToOriginal(source, row.FullPath), row.StableKey));
        var explicitRows = new List<CatalogProjectionRow>();
        await foreach (var row in query.EnumerateSelectionAsync(new(false, [Path.Combine(root, "a")]))) explicitRows.Add(row);
        Assert.EndsWith("a", Assert.Single(explicitRows).FullPath);
        var invertedRows = new List<CatalogProjectionRow>();
        await foreach (var row in query.EnumerateSelectionAsync(new(true, [Path.Combine(root, "a")]))) invertedRows.Add(row);
        Assert.EndsWith("b", Assert.Single(invertedRows).FullPath);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("C:/other")]
    [InlineData("a//b")]
    public void RootLinkingRejectsUnsafeRelativePaths(string relative) =>
        Assert.Throws<InvalidDataException>(() => WorkspacePathMapping.ValidateRelative(relative));

    [Fact]
    public async Task ReadOnlyArchiveQueryDoesNotChangeDatabaseBytes()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Path, "source.db");
        using (var store = new SqliteCatalogProjectionStore(database))
        {
            using var writer = store.Begin(Scope("profile", directory.Path, database), [Field("text", ProfileFieldValueKind.String)]);
            writer.Append(Item(Path.Combine(directory.Path, "item"), "item", new() { ["text"] = "x" }));
            writer.Publish(Summary(1));
        }
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=false"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; command.ExecuteNonQuery();
        }
        var before = await File.ReadAllBytesAsync(database);
        var snapshot = SqliteCatalogProjectionStore.ValidateTransferredSnapshot(database);
        using (var store = new SqliteCatalogProjectionStore(database, readOnly: true))
        {
            await using var query = store.OpenQuery(snapshot, new());
            Assert.Equal(1, await query.CountAsync());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
    }

    private static CatalogProjectionScope Scope(string id, string root, string db) => new(Guid.NewGuid().ToString("N"), id, root, db, "fingerprint", "en-US", Guid.NewGuid());
    private static ProfileFieldDescriptor Field(string id, ProfileFieldValueKind kind) => new(id, id, id, 0, false, kind, true, null, null);
    private static CatalogItem Item(string full, string relative, Dictionary<string, object?> values) => new(full, relative, "rule", values, values, false);
    private static CatalogProjectionSummary Summary(int count) => new(count, count, 0, 0, 0, false, TimeSpan.Zero, DateTimeOffset.UtcNow);
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fe-transfer-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
