using System.Runtime.CompilerServices;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class CatalogSelectionMatchingTests
{
    [Fact]
    public async Task MatchesFullFilteredQueryUsingAliasesExactNumbersDatesAndBooleanThenExportsBaseline()
    {
        using var fixture = new Fixture();
        var day = new DateTime(2026, 5, 21);
        var tiny = 0.0000000000000000000000000001m;
        var fields = new[] { Field("alias") with { ValueMappings = new Dictionary<string, string> { ["A"] = "Alice" } },
            Field("amount", ProfileFieldValueKind.Decimal), Field("date", ProfileFieldValueKind.DateTime), Field("flag", ProfileFieldValueKind.Boolean), Field("note") with { SourceKind = ProfileFieldSourceKind.UserInput } };
        var snapshot = fixture.Publish(fields,
            ("a", Values("A", tiny, day.AddHours(5), true)), ("b", Values("A", tiny, day.AddHours(9), true)),
            ("hidden", Values("A", tiny, day, true)), ("large", Values("A", tiny * 2, day, true)),
            ("false", Values("A", tiny, day, false)), ("different", Values("B", tiny, day, true)));
        await fixture.Store.SetValueAsync("profile", fixture.PathFor("a"), "note", "edited");
        await fixture.Store.SetValueAsync("profile", fixture.PathFor("b"), "note", "edited");
        var columns = new[] { new CatalogSelectionMatchColumn("alias"), new("amount"), new("date"), new("flag"), new("note") };
        await using var query = fixture.Store.OpenQuery(snapshot, new() { Filters = [new(CatalogColumnTarget.Field("note"), CatalogFilterOperator.Contains) { Text = "edited" }] });
        var matcher = Assert.IsAssignableFrom<ICatalogSelectionMatchQuery>(query);
        using var result = await matcher.CreateSelectionMatchAsync(new(columns, InputRows(
            [" alice ", "0.0000000000000000000000000001", "2026-05-21", "true", "edited"],
            ["ALICE", "0.0000000000000000000000000001", "2026-05-21", "True", "edited"],
            ["", "0", "invalid", "true", "edited"],
            ["Nobody", "1", "2026-05-21", "true", "edited"]), "en-US"));
        Assert.Equal(2, result.Summary.MatchingItems); Assert.Equal(1, result.Summary.DuplicateInputRows);
        Assert.Equal(1, result.Summary.InvalidRows); Assert.Equal(1, result.Summary.UnmatchedInputRows);
        Assert.Equal(1, result.Summary.AmbiguousInputRows);
        using var selection = new QuerySelection(); selection.ReplaceBaseline(result.Keys);
        Assert.Empty(selection.Keys); Assert.Equal(2, selection.Count(2));
        var rows = await Collect(query, selection); Assert.Equal(new[] { "a", "b" }, rows.Select(row => row.RelativePath));
        selection.Set(fixture.PathFor("a"), false); Assert.Equal("b", Assert.Single(await Collect(query, selection)).RelativePath);
        selection.Invert(); Assert.Equal("a", Assert.Single(await Collect(query, selection)).RelativePath);
    }

    [Fact]
    public async Task OriginalValuesAndExactDateCanBeMatchedAndNullNeverMatchesDisplayDash()
    {
        using var fixture = new Fixture();
        var day = new DateTime(2026, 5, 21, 11, 30, 0);
        var snapshot = fixture.Publish([Field("alias") with { ValueMappings = new Dictionary<string, string> { ["A"] = "Alice" } }, Field("date", ProfileFieldValueKind.DateTime)],
            ("a", new() { ["alias"] = "A", ["date"] = day }), ("b", new() { ["alias"] = "A", ["date"] = day.AddMinutes(1) }), ("null", new() { ["alias"] = null, ["date"] = day }));
        await using var query = fixture.Store.OpenQuery(snapshot, new());
        var matcher = (ICatalogSelectionMatchQuery)query;
        using var exact = await matcher.CreateSelectionMatchAsync(new([new("alias", ProfileSelectionValueSource.Original), new("date", DateComparison: ProfileSelectionDateComparison.Exact)],
            InputRows(["A", "2026-05-21T11:30:00"]), "en-US"));
        Assert.Equal(1, exact.Summary.MatchingItems);
        using var missing = await matcher.CreateSelectionMatchAsync(new([new("alias")], InputRows(["—"]), "en-US"));
        Assert.Equal(0, missing.Summary.MatchingItems);
    }

    [Fact]
    public async Task KeysetsUseMappedStableIdentitySurviveResultDisposalAndCleanUpAfterSelectionRelease()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Publish([Field("name")], ("a", new() { ["name"] = "item" }));
        await using var query = fixture.Store.OpenQuery(snapshot, new());
        var original = Path.Combine(Path.GetTempPath(), "original", "a");
        var result = await ((ICatalogSelectionMatchQuery)query).CreateSelectionMatchAsync(new([new("name")], InputRows(["item"]), "en-US"), _ => original);
        var path = result.Keys.DatabasePath;
        using var selection = new QuerySelection(); selection.ReplaceBaseline(result.Keys); result.Dispose();
        Assert.True(File.Exists(path)); Assert.True(selection.Contains(original.ToUpperInvariant()));
        using (var mapped = selection.MapKeys(_ => fixture.PathFor("a"))) Assert.Single(await Collect(query, mapped));
        selection.Clear(); Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LargeSelectionIncludesUnloadedRowsAndRetainsOnlyExplicitOverridesInMemory()
    {
        using var fixture = new Fixture();
        var count = 10_001;
        var snapshot = fixture.Publish([Field("group")], Enumerable.Range(0, count).Select(index => (index.ToString("D6"),
            new Dictionary<string, object?> { ["group"] = index % 2 == 0 ? "A" : "B" })).ToArray());
        await using var query = fixture.Store.OpenQuery(snapshot, new());
        using var result = await ((ICatalogSelectionMatchQuery)query).CreateSelectionMatchAsync(new([new("group")], InputRows(["A"]), "en-US"));
        Assert.Equal(5001, result.Summary.MatchingItems);
        using var selection = new QuerySelection(); selection.ReplaceBaseline(result.Keys);
        Assert.Empty(selection.Keys); Assert.True(selection.Contains(fixture.PathFor("010000")));
        Assert.Equal(5001, (await Collect(query, selection)).Count);
        selection.Invert(); Assert.Equal(5000, selection.Count(count)); Assert.Equal(5000, (await Collect(query, selection)).Count);
        selection.SelectAll(); Assert.Equal(count, selection.Count(count)); selection.Clear(); Assert.Equal(0, selection.Count(count));
    }

    [Fact]
    public async Task CancelledMatchingKeepsExistingSelectionAndStagingQueriesAreRejected()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Publish([Field("name")], ("a", new() { ["name"] = "item" }));
        using var selection = new QuerySelection(false, [fixture.PathFor("a")]);
        await using var query = fixture.Store.OpenQuery(snapshot, new());
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((ICatalogSelectionMatchQuery)query).CreateSelectionMatchAsync(new([new("name")], InputRows(["item"]), "en-US"), cancellationToken: token.Token));
        Assert.True(selection.Contains(fixture.PathFor("a")));
        using var writer = fixture.Store.Begin(fixture.Scope, [Field("name")]); writer.Flush();
        await using var building = fixture.Store.OpenBuildingQuery(writer, fixture.Scope, [Field("name")], new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((ICatalogSelectionMatchQuery)building).CreateSelectionMatchAsync(new([new("name")], InputRows(["item"]), "en-US")));
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("cell")]
    [InlineData("notes")]
    public async Task FreshApplyValidationRejectsPreviewAfterExternalMutationAndKeepsExistingChecks(string mutation)
    {
        using var fixture = new Fixture();
        var fields = new[] { Field("name") with { SourceKind = ProfileFieldSourceKind.TextFileContent },
            Field("note") with { SourceKind = ProfileFieldSourceKind.UserInput } };
        var snapshot = fixture.Publish(fields, ("previous", new() { ["name"] = "old" }), ("preview", new() { ["name"] = "match" }));
        await using var query = fixture.Store.OpenQuery(snapshot, new());
        using var preview = await ((ICatalogSelectionMatchQuery)query).CreateSelectionMatchAsync(new([new("name")], InputRows(["match"]), "en-US"));
        using var selection = new QuerySelection(false, [fixture.PathFor("previous")]);
        Assert.True(preview.Keys.Contains(fixture.PathFor("preview")));
        switch (mutation)
        {
            case "publication":
                fixture.Publish(fields, ("previous", new() { ["name"] = "old" }), ("preview", new() { ["name"] = "changed" }));
                break;
            case "cell":
                await fixture.Store.UpdateTextCellAsync(snapshot, fixture.PathFor("preview"), "name", "changed",
                    new(ProfileFieldResourceState.Present, "note.txt"));
                break;
            case "notes":
                await fixture.Store.SetValueAsync("profile", fixture.PathFor("preview"), "note", "changed");
                break;
        }
        await Assert.ThrowsAsync<ResultQueryChangedException>(async () =>
        {
            await query.CountAsync(); // Same fresh read performed by the Apply handler.
            selection.ReplaceBaseline(preview.Keys);
        });
        Assert.True(selection.Contains(fixture.PathFor("previous")));
        Assert.False(selection.Contains(fixture.PathFor("preview")));
        Assert.Null(selection.Baseline); Assert.Equal(1, selection.Count(2));
    }

    private static Dictionary<string, object?> Values(string alias, decimal amount, DateTime date, bool flag) =>
        new() { ["alias"] = alias, ["amount"] = amount, ["date"] = date, ["flag"] = flag };
    private static ProfileFieldDescriptor Field(string id, ProfileFieldValueKind kind = ProfileFieldValueKind.String) => new(id, id, id, 0, false, kind, true, null, null);
    private static async IAsyncEnumerable<CatalogSelectionInputRow> InputRows(params string?[][] rows)
    { for (var index = 0; index < rows.Length; index++) { yield return new(index + 1, rows[index]); await Task.CompletedTask; } }
    private static async Task<List<CatalogProjectionRow>> Collect(IResultQuerySession<CatalogProjectionRow> query, QuerySelection selection)
    { var rows = new List<CatalogProjectionRow>(); await foreach (var row in query.EnumerateSelectionAsync(selection)) rows.Add(row); return rows; }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("fe-selection-tests-").FullName;
        public SqliteCatalogProjectionStore Store { get; }
        public CatalogProjectionScope Scope { get; }
        public Fixture()
        { Store = new(Path.Combine(Directory, "catalog.db")); Scope = new("selection-test", "profile", Directory, Path.Combine(Directory, "engine.db"), "fingerprint", "en-US", Guid.NewGuid()); }
        public string PathFor(string name) => Path.Combine(Directory, name);
        public CatalogProjectionSnapshot Publish(IReadOnlyList<ProfileFieldDescriptor> fields, params (string Name, Dictionary<string, object?> Values)[] rows)
        {
            using var writer = Store.Begin(Scope, fields);
            foreach (var row in rows) writer.Append(new CatalogItem(PathFor(row.Name), row.Name, "rule", row.Values, row.Values, false));
            return writer.Publish(new(rows.Length, rows.Length, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
        }
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
