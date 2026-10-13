using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Persistence.Tests;

[CollectionDefinition("Projection allocation measurements", DisableParallelization = true)]
public sealed class ProjectionAllocationCollection { }

[Collection("Projection allocation measurements")]
public sealed class CatalogProjectionByteBudgetTests
{
    [Fact]
    public async Task OversizedMultiCellRowIsReturnedAloneAndEnumerationNeverSkipsTheFollowingRow()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("first"), Field("second") };
        var text = new string('x', 4 * 1024 * 1024);
        using var writer = workspace.Store.Begin(workspace.Scope, fields);
        for (var index = 0; index < 6; index++) writer.Append(workspace.Item(index, new() { ["first"] = text, ["second"] = text }));
        var snapshot = writer.Publish(Summary(6));
        await using var query = workspace.Store.OpenQuery(snapshot, new());
        var page = await query.ReadRangeAsync(0, 500);
        Assert.True(page.HasMore);
        Assert.Single(page.Items);
        var actual = new List<string>();
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true)))
        {
            Assert.Equal(text.Length, ((string)row.Values["first"]!).Length);
            Assert.Equal(text.Length, ((string)row.Values["second"]!).Length);
            actual.Add(row.RelativePath);
        }
        Assert.Equal(Enumerable.Range(0, 6).Select(index => $"row-{index:D4}"), actual);
    }

    [Fact]
    public async Task RemovedFieldNotesAreNotReadOrAllocatedForCurrentRows()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "" } };
        using var writer = workspace.Store.Begin(workspace.Scope, fields);
        var row = workspace.Item(0, new()); writer.Append(row); var snapshot = writer.Publish(Summary(1));
        await workspace.Store.SetValueAsync(workspace.Scope.ProfileId, row.FullPath, "memo", "current");
        using (var connection = new SqliteConnection($"Data Source={workspace.Store.DatabasePath};Pooling=false"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO user_inputs(profile_key,path_key,field_id,value,value_search) VALUES($profile,$path,$field,$value,$value);";
            command.Parameters.AddWithValue("$profile", workspace.Scope.ProfileId);
            command.Parameters.AddWithValue("$path", row.FullPath.ToUpperInvariant());
            var field = command.Parameters.Add("$field", SqliteType.Text);
            command.Parameters.AddWithValue("$value", new string('x', 256)); command.Prepare();
            for (var index = 0; index < 4096; index++) { field.Value = "retired" + index; command.ExecuteNonQuery(); }
            transaction.Commit();
        }
        await using var query = workspace.Store.OpenQuery(snapshot, new());
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var page = await query.ReadRangeAsync(0, 500);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal("current", Assert.Single(page.Items).Values["memo"]);
        Assert.True(allocated < 2L * 1024 * 1024, $"Retired notes inflated a one-row page by {allocated:N0} bytes.");
    }

    [Fact]
    public async Task SavedNotesAreBudgetedBeforeBulkLookupAndAllocationIsBoundedToReturnedRows()
    {
        using var workspace = new Workspace();
        var fields = Enumerable.Range(0, ProfileManifestLimits.MaximumUserInputFieldCount).Select(index =>
            Field($"memo{index}") with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "" }).ToArray();
        using var writer = workspace.Store.Begin(workspace.Scope, fields);
        for (var index = 0; index < 500; index++) writer.Append(workspace.Item(index, new()));
        var snapshot = writer.Publish(Summary(500));
        var note = new string('x', ProfileManifestLimits.MaximumUserInputValueLength);
        using (var connection = new SqliteConnection($"Data Source={workspace.Store.DatabasePath};Pooling=false"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO user_inputs(profile_key,path_key,field_id,value,value_search) VALUES($profile,$path,$field,$value,$value);";
            command.Parameters.AddWithValue("$profile", workspace.Scope.ProfileId);
            var path = command.Parameters.Add("$path", SqliteType.Text); var field = command.Parameters.Add("$field", SqliteType.Text);
            command.Parameters.AddWithValue("$value", note); command.Prepare();
            for (var index = 0; index < 500; index++)
            {
                path.Value = workspace.Item(index, new()).FullPath.ToUpperInvariant();
                foreach (var descriptor in fields) { field.Value = descriptor.FieldId; command.ExecuteNonQuery(); }
            }
            transaction.Commit();
        }
        await using var query = workspace.Store.OpenQuery(snapshot, new());
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var page = await query.ReadRangeAsync(0, 500);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(page.HasMore);
        Assert.InRange(page.Items.Count, 1, 64);
        Assert.True(allocated < 24L * 1024 * 1024, $"A byte-bounded note page allocated {allocated:N0} bytes.");
        Assert.All(page.Items, row => Assert.All(fields, descriptor => Assert.Equal(note, row.Values[descriptor.FieldId])));
        var count = 0;
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true)))
        {
            Assert.Equal($"row-{count:D4}", row.RelativePath);
            Assert.All(fields, descriptor => Assert.Equal(note, row.Values[descriptor.FieldId]));
            count++;
        }
        Assert.Equal(500, count);
    }

    private static ProfileFieldDescriptor Field(string id) => new(id, id, id, 0, false, ProfileFieldValueKind.String, true, null, null);
    private static CatalogProjectionSummary Summary(int count) => new(count, count, 0, 0, 0, false, null, DateTimeOffset.UtcNow);
    private sealed class Workspace : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "findeverything-byte-budget-" + Guid.NewGuid().ToString("N"));
        public SqliteCatalogProjectionStore Store { get; }
        public CatalogProjectionScope Scope { get; }
        public Workspace()
        {
            Directory.CreateDirectory(_directory); Store = new(Path.Combine(_directory, "catalog.db"));
            Scope = new("bytes", "bytes", _directory, Path.Combine(_directory, "metadata.db"), "bytes", "en-US", null);
        }
        public CatalogItem Item(int index, Dictionary<string, object?> values) =>
            new(Path.Combine(_directory, $"row-{index:D4}"), $"row-{index:D4}", "rule", values, values, false);
        public void Dispose() { Store.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
