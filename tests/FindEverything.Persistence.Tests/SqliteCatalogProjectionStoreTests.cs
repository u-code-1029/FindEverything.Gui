using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class SqliteCatalogProjectionStoreTests
{
    [Fact]
    public async Task Selected_range_pages_preserve_sort_and_baseline_overrides_without_full_row_enumeration()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var rows = Enumerable.Range(0, 1500).Select(index => workspace.Item($"row-{index:D4}", new() { ["name"] = $"value-{index:D4}" })).ToArray();
        var snapshot = Publish(store, workspace.Scope, [Field("name")], rows);
        await using var original = store.OpenQuery(snapshot, new());
        using var all = new QuerySelection(true);
        using var keys = await SqliteSelectionSnapshot.CreateAsync(original, all, row => row.FullPath, row => row.FullPath);
        using var selected = new QuerySelection(); selected.ReplaceBaseline(keys);
        selected.Set(rows[1499].FullPath, false);
        await using var review = store.OpenQuery(snapshot, new(Descending: true));
        var first = await review.ReadSelectionRangeAsync(selected, 0, 2);
        Assert.Equal(["row-1498", "row-1497"], first.Items.Select(row => row.RelativePath));
        var deep = await review.ReadSelectionRangeAsync(selected, 1497, 10);
        Assert.Equal(["row-0001", "row-0000"], deep.Items.Select(row => row.RelativePath));
        Assert.False(deep.HasMore);
        Assert.Equal(1499, await review.CountSelectionAsync(selected));
    }

    [Fact]
    public async Task Selection_count_uses_current_filter_without_losing_hidden_checked_items()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var rows = new[] { workspace.Item("first", new() { ["name"] = "alpha" }),
            workspace.Item("second", new() { ["name"] = "beta" }), workspace.Item("third", new() { ["name"] = "alpha" }) };
        var snapshot = Publish(store, workspace.Scope, [Field("name")], rows);
        await using var allQuery = store.OpenQuery(snapshot, new());
        using var all = new QuerySelection(true);
        using var baseline = await SqliteSelectionSnapshot.CreateAsync(allQuery, all, row => row.FullPath, row => row.FullPath);
        using var selection = new QuerySelection(); selection.ReplaceBaseline(baseline);
        selection.Set(rows[2].FullPath, false);
        await using var filtered = store.OpenQuery(snapshot, new("alpha"));
        Assert.Equal(1, await filtered.CountSelectionAsync(selection));
        Assert.Equal(2, selection.Count(long.MaxValue));
        Assert.Equal(2, await allQuery.CountSelectionAsync(selection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_path_paging_walks_the_primary_key_without_a_temporary_sort(bool descending)
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var snapshot = Publish(store, workspace.Scope, [Field("name")], Enumerable.Range(0, 48)
            .Reverse().Select(index => workspace.Item($"item-{index:D3}", new() { ["name"] = index.ToString() })).ToArray());
        await using var query = store.OpenQuery(snapshot, new(Descending: descending));
        var page = await query.ReadRangeAsync(11, 5);
        var expected = Enumerable.Range(0, 48).Select(index => $"item-{index:D3}");
        if (descending) expected = expected.Reverse();
        Assert.Equal(expected.Skip(11).Take(5), page.Items.Select(row => row.RelativePath));
        // Inspect the actual query builder, not a second SQL statement that could
        // accidentally stay fast while production reintroduces a full sort.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.Database};Pooling=false");
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var builder = query.GetType().GetMethod("BuildCommand", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var command = (Microsoft.Data.Sqlite.SqliteCommand)builder.Invoke(query, [connection, transaction, "user_inputs", null, 11, 5])!;
        command.CommandText = "EXPLAIN QUERY PLAN SELECT r.full_path " + command.CommandText;
        using var reader = command.ExecuteReader();
        var plans = new List<string>();
        while (reader.Read()) plans.Add(reader.GetString(3));
        Assert.Contains(plans, value => value.Contains("PRIMARY KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(plans, value => value.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CacheRequiresExactProfileFingerprintCultureAndIndexGeneration()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("alias") };
        using var writer = store.Begin(workspace.Scope, fields);
        writer.Append(workspace.Item("first", new() { ["alias"] = "A" }));
        var snapshot = writer.Publish(Summary(1));

        var found = Assert.IsType<CatalogProjectionSnapshot>(await store.FindAsync(workspace.Scope));
        Assert.Equal(snapshot.Generation, found.Generation);
        Assert.Equal("alias", Assert.Single(found.Fields).FieldId);
        Assert.Null(await store.FindAsync(workspace.Scope with { Fingerprint = "changed" }));
        Assert.Null(await store.FindAsync(workspace.Scope with { CultureName = "ko-KR" }));
        Assert.Null(await store.FindAsync(workspace.Scope with { IndexScanId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task EarlierProjectionSchemaUpgradePreservesPublishedRowsAndUserEdits()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("name") };
        var row = workspace.Item("first", new() { ["name"] = "preserved" });
        string generation;
        using (var original = new SqliteCatalogProjectionStore(workspace.Database))
        {
            generation = Publish(original, workspace.Scope, fields, row).Generation;
            await original.SetValueAsync(workspace.Scope.ProfileId, row.FullPath, "memo", "saved note");
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.Database};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX generations_logical_key; ALTER TABLE projection_generations DROP COLUMN logical_key; ALTER TABLE projection_generations DROP COLUMN revision; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new SqliteCatalogProjectionStore(workspace.Database);
        var found = (await upgraded.FindAsync(workspace.Scope))!;
        Assert.Equal(generation, found.Generation);
        await using var query = upgraded.OpenQuery(found, new());
        Assert.Equal("preserved", Assert.Single((await query.ReadRangeAsync(0, 500)).Items).Values["name"]);
        Assert.Equal("saved note", upgraded.GetValues(workspace.Scope.ProfileId, row.FullPath)["memo"]);
    }

    [Fact]
    public async Task UnpublishedAndCancelledWriterNeverReplaceExistingPublication()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("name") };
        var initial = Publish(store, workspace.Scope, fields, workspace.Item("old", new() { ["name"] = "old" }));
        await using var oldQuery = store.OpenQuery(initial, new());
        using (var replacement = store.Begin(workspace.Scope, fields))
        {
            replacement.Append(workspace.Item("new", new() { ["name"] = "new" }));
            replacement.Flush();
            await using var building = store.OpenBuildingQuery(replacement, workspace.Scope, fields, new());
            Assert.Equal(1, await building.CountAsync());
            Assert.EndsWith("new", Assert.Single((await building.ReadRangeAsync(0, 500)).Items).FullPath);
            Assert.EndsWith("old", Assert.Single((await oldQuery.ReadRangeAsync(0, 500)).Items).FullPath);
        }
        Assert.Equal(initial.Generation, (await store.FindAsync(workspace.Scope))!.Generation);
        Assert.Equal(1, await oldQuery.CountAsync());
    }

    [Fact]
    public void LargeTextRowsFlushByByteBudgetBeforeFiveHundredItems()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
        using var writer = store.Begin(workspace.Scope, fields);
        var text = new string('x', 1024 * 1024);
        writer.Append(workspace.Item("first", new() { ["text"] = text }));
        writer.Append(workspace.Item("second", new() { ["text"] = text }));
        Assert.Equal(2, writer.Count);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.Database};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM projection_rows;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task DisplayTextPreviewIsTruncatedInSqlButFilteringAndOutputUseEntireValue()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[]
        {
            Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent },
            Field("name"), Field("count", ProfileFieldValueKind.Int32)
        };
        var content = new string('가', 5000) + "\"quoted\"\nTAIL_ONLY";
        var snapshot = Publish(store, workspace.Scope, fields,
            workspace.Item("row", new() { ["text"] = content, ["name"] = new string('b', 6000), ["count"] = 42 }));
        await using var query = store.OpenQuery(snapshot, new("TAIL_ONLY") { TextPreviewCharacters = 2048 });
        Assert.Equal(1, await query.CountAsync());
        var row = Assert.Single((await query.ReadRangeAsync(0, 500)).Items);
        Assert.Equal(new string('가', 2048), row.Values["text"]);
        Assert.Equal(new string('b', 6000), row.Values["name"]);
        Assert.Equal(42, row.Values["count"]);
        Assert.Equal(new[] { "text" }, row.PreviewFields);
        await foreach (var full in query.EnumerateSelectionAsync(new QuerySelection(true)))
        {
            Assert.Equal(content, full.Values["text"]);
            Assert.Empty(full.PreviewFields);
        }
    }

    [Fact]
    public async Task FullValueOutputUsesByteBoundedPagesAndNeverDropsRowsAtBoundary()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
        var text = new string('x', 1024 * 1024);
        var items = Enumerable.Range(0, 12).Select(index => workspace.Item($"row-{index:D2}", new() { ["text"] = text })).ToArray();
        var snapshot = Publish(store, workspace.Scope, fields, items);
        await using var query = store.OpenQuery(snapshot, new());
        var first = await query.ReadRangeAsync(0, 500);
        Assert.True(first.HasMore);
        Assert.InRange(first.Items.Count, 1, 4);
        var count = 0;
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true)))
        {
            Assert.Equal(text.Length, ((string)row.Values["text"]!).Length);
            count++;
        }
        Assert.Equal(12, count);
    }

    [Fact]
    public async Task BuildingResetInvalidatesOldLiveQueryAndSecondStoreDoesNotDeleteItsRows()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("name") };
        using var writer = store.Begin(workspace.Scope, fields);
        writer.Append(workspace.Item("first", new() { ["name"] = "first" }));
        await using var live = store.OpenBuildingQuery(writer, workspace.Scope, fields, new());
        using var secondStore = new SqliteCatalogProjectionStore(workspace.Database);
        Assert.Equal(1, await live.CountAsync());
        writer.Reset();
        writer.Append(workspace.Item("second", new() { ["name"] = "second" }));
        writer.Flush();
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => live.ReadRangeAsync(0, 500));
        await using var replacementLive = store.OpenBuildingQuery(writer, workspace.Scope, fields, new());
        Assert.NotEqual(live.Identity, replacementLive.Identity);
        Assert.EndsWith("second", Assert.Single((await replacementLive.ReadRangeAsync(0, 500)).Items).FullPath);
    }

    [Fact]
    public async Task FinalPhysicalScanIdentityIsSavedAndStaleWriterCannotOverwriteAnotherPublisher()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("name") };
        using var first = store.Begin(workspace.Scope, fields);
        using var stale = store.Begin(workspace.Scope, fields);
        first.Append(workspace.Item("first", new() { ["name"] = "first" }));
        var finalScope = workspace.Scope with { IndexScanId = Guid.NewGuid() };
        var published = first.Publish(Summary(1), finalScope);
        Assert.Equal(finalScope, published.Scope);
        Assert.Equal(published.Generation, (await store.FindAsync(finalScope))!.Generation);
        Assert.Null(await store.FindAsync(workspace.Scope));
        stale.Append(workspace.Item("stale", new() { ["name"] = "stale" }));
        Assert.Throws<ResultQueryChangedException>(() => stale.Publish(Summary(1)));
        Assert.Equal(published.Generation, (await store.FindAsync(finalScope))!.Generation);
    }

    [Fact]
    public async Task ChangedFingerprintRetiresOldLogicalScopeAndUserFieldsDoNotShadowPathSort()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("fullpath") };
        var first = Publish(store, workspace.Scope, fields,
            workspace.Item("a", new() { ["fullpath"] = "z" }),
            workspace.Item("z", new() { ["fullpath"] = "a" }));
        await using var old = store.OpenQuery(first, new());
        var changedScope = workspace.Scope with { Key = "new-fingerprint-key", Fingerprint = "new-definition" };
        var current = Publish(store, changedScope, fields,
            workspace.Item("a", new() { ["fullpath"] = "z" }),
            workspace.Item("z", new() { ["fullpath"] = "a" }));
        Assert.Null(await store.FindAsync(workspace.Scope));
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => old.CountAsync());
        await using var path = store.OpenQuery(current, new(SortFieldId: "FullPath"));
        Assert.EndsWith("a", (await path.ReadRangeAsync(0, 500)).Items[0].FullPath);
        await using var userField = store.OpenQuery(current, new(SortFieldId: "fullpath"));
        Assert.EndsWith("z", (await userField.ReadRangeAsync(0, 500)).Items[0].FullPath);
    }

    [Fact]
    public async Task ValuesRoundTripAndSortByNumbersDatesAndMappedText()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[]
        {
            Field("number", ProfileFieldValueKind.Decimal),
            Field("date", ProfileFieldValueKind.DateTime),
            Field("alias") with { ValueMappings = new Dictionary<string,string> { ["A"] = "Zebra", ["B"] = "Alpha" } },
            Field("count", ProfileFieldValueKind.Int32), Field("flag", ProfileFieldValueKind.Boolean), Field("missing")
        };
        var date = new DateTime(2026, 5, 21, 1, 2, 3, DateTimeKind.Unspecified);
        var snapshot = Publish(store, workspace.Scope, fields,
            workspace.Item("first", new() { ["number"] = 0.0000000000000000000000000002m, ["date"] = date, ["alias"] = "A", ["count"] = 7, ["flag"] = true, ["missing"] = null }),
            workspace.Item("second", new() { ["number"] = 0.0000000000000000000000000001m, ["date"] = date.AddDays(-1), ["alias"] = "B", ["count"] = 2, ["flag"] = false }));
        await using var numeric = store.OpenQuery(snapshot, new(SortFieldId: "number"));
        var rows = (await numeric.ReadRangeAsync(0, 500)).Items;
        Assert.EndsWith("second", rows[0].FullPath);
        Assert.Equal(0.0000000000000000000000000002m, Assert.IsType<decimal>(rows[1].Values["number"]));
        Assert.Equal(date, Assert.IsType<DateTime>(rows[1].Values["date"]));
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)rows[1].Values["date"]!).Kind);
        Assert.Equal(7, Assert.IsType<int>(rows[1].Values["count"]));
        Assert.True(Assert.IsType<bool>(rows[1].Values["flag"]));
        Assert.Null(rows[1].Values["missing"]);
        await using var mapped = store.OpenQuery(snapshot, new(SortFieldId: "alias"));
        Assert.EndsWith("second", (await mapped.ReadRangeAsync(0, 500)).Items[0].FullPath);
        await using var original = store.OpenQuery(snapshot, new(ShowOriginalValues: true, SortFieldId: "alias"));
        Assert.EndsWith("first", (await original.ReadRangeAsync(0, 500)).Items[0].FullPath);
        await using var dates = store.OpenQuery(snapshot, new(SortFieldId: "date", Descending: true));
        Assert.EndsWith("first", (await dates.ReadRangeAsync(0, 500)).Items[0].FullPath);
    }

    [Fact]
    public async Task SQLFilterUsesLiteralTermsMappedModeAndCurrentOrDefaultUserInputs()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[]
        {
            Field("alias") with { ValueMappings = new Dictionary<string,string> { ["A"] = "홍길동" } },
            Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default memo" }
        };
        var row = workspace.Item("budget_100%'", new() { ["alias"] = "A" });
        var snapshot = Publish(store, workspace.Scope, fields, row);
        await using (var defaults = store.OpenQuery(snapshot, new("홍길동 default _100%'")))
            Assert.Equal(1, await defaults.CountAsync());
        await store.SetValueAsync(workspace.Scope.ProfileId, row.FullPath, "memo", "saved note");
        await using var noteQuery = store.OpenQuery(snapshot, new("saved 홍길동"));
        var found = Assert.Single((await noteQuery.ReadRangeAsync(0, 500)).Items);
        Assert.Equal("saved note", found.Values["memo"]);
        await using var removedDefault = store.OpenQuery(snapshot, new("default"));
        Assert.Equal(0, await removedDefault.CountAsync());
        await using var raw = store.OpenQuery(snapshot, new("홍길동", ShowOriginalValues: true));
        Assert.Equal(0, await raw.CountAsync());
        using var reopened = new SqliteCatalogProjectionStore(workspace.Database);
        Assert.Equal("saved note", reopened.GetValues(workspace.Scope.ProfileId, row.FullPath)["memo"]);
    }

    [Fact]
    public async Task InvalidUserInputNeverReplacesPreviouslySavedValue()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var path = workspace.Item("row", new()).FullPath;
        await store.SetValueAsync(workspace.Scope.ProfileId, path, "memo", "original");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetValueAsync(workspace.Scope.ProfileId,
            path, "memo", new string('x', ProfileManifestLimits.MaximumUserInputValueLength + 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetValueAsync(workspace.Scope.ProfileId, "relative", "memo", "new"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetValueAsync(workspace.Scope.ProfileId, path, "invalid field", "new"));
        Assert.Equal("original", store.GetValues(workspace.Scope.ProfileId, path)["memo"]);
        await store.SetValueAsync(workspace.Scope.ProfileId, path, "memo", string.Empty);
        Assert.Equal(string.Empty, store.GetValues(workspace.Scope.ProfileId, path)["memo"]);
    }

    [Fact]
    public async Task NoteEditsInvalidateFilteredPagesButNoOpAndOtherProfilesDoNot()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default" } };
        var first = workspace.Item("a", new());
        var snapshot = Publish(store, workspace.Scope, fields, first, workspace.Item("b", new()));
        await using var query = store.OpenQuery(snapshot, new("default", SortFieldId: "memo"));
        Assert.Equal(2, await query.CountAsync());
        await store.SetValueAsync(workspace.Scope.ProfileId, first.FullPath, "memo", "changed");
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => query.CountAsync());
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => query.ReadRangeAsync(1, 500));
        await using var fresh = store.OpenQuery(snapshot, new("default", SortFieldId: "memo"));
        Assert.Equal(1, await fresh.CountAsync());
        await store.SetValueAsync(workspace.Scope.ProfileId, first.FullPath, "memo", "changed");
        await store.SetValueAsync("another-profile", first.FullPath, "memo", "other profile value");
        Assert.Equal(1, await fresh.CountAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetValueAsync(workspace.Scope.ProfileId,
            first.FullPath, "memo", new string('x', ProfileManifestLimits.MaximumUserInputValueLength + 1)));
        Assert.Equal(1, await fresh.CountAsync());
    }

    [Fact]
    public async Task OutputFreezesNotesAfterInitialRevisionValidationAcrossMultiplePages()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput } };
        var items = Enumerable.Range(0, 510).Select(index => workspace.Item($"row-{index:D4}", new())).ToArray();
        var snapshot = Publish(store, workspace.Scope, fields, items);
        await using var query = store.OpenQuery(snapshot, new());
        await using var output = query.EnumerateSelectionAsync(new QuerySelection(true)).GetAsyncEnumerator();
        Assert.True(await output.MoveNextAsync());
        await store.SetValueAsync(workspace.Scope.ProfileId, items[505].FullPath, "memo", "changed after output began");
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => query.CountAsync());
        var count = 1;
        while (await output.MoveNextAsync())
        {
            Assert.Equal(string.Empty, output.Current.Values["memo"]);
            count++;
        }
        Assert.Equal(510, count);
    }

    [Fact]
    public async Task PublicationInvalidatesNormalPagesButOutputKeepsItsRowsAndNotes()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput } };
        var items = Enumerable.Range(0, 510).Select(index => workspace.Item($"row-{index:D4}", new())).ToArray();
        var snapshot = Publish(store, workspace.Scope, fields, items);
        await using var query = store.OpenQuery(snapshot, new());
        await using var output = query.EnumerateSelectionAsync(new QuerySelection(true)).GetAsyncEnumerator();
        Assert.True(await output.MoveNextAsync());
        Publish(store, workspace.Scope, fields, workspace.Item("replacement", new()));
        await store.SetValueAsync(workspace.Scope.ProfileId, items[505].FullPath, "memo", "edited after output started");
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => query.ReadRangeAsync(0, 500));
        var count = 1;
        while (await output.MoveNextAsync())
        {
            Assert.Equal(string.Empty, output.Current.Values["memo"]);
            count++;
        }
        Assert.Equal(510, count);
    }

    [Fact]
    public async Task AllMatchingSelectionIncludesUnloadedRowsAndExceptionsInCurrentSortOrder()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("number", ProfileFieldValueKind.Int32) };
        var items = Enumerable.Range(0, 1005).Select(index => workspace.Item($"row-{index:D4}", new() { ["number"] = index })).ToArray();
        var snapshot = Publish(store, workspace.Scope, fields, items);
        await using var query = store.OpenQuery(snapshot, new(SortFieldId: "number", Descending: true));
        var selection = new QuerySelection(true);
        selection.Set(items[500].FullPath, false);
        var numbers = new List<int>();
        await foreach (var row in query.EnumerateSelectionAsync(selection)) numbers.Add((int)row.Values["number"]!);
        Assert.Equal(1004, numbers.Count);
        Assert.Equal(1004, numbers[0]);
        Assert.Equal(0, numbers[^1]);
        Assert.DoesNotContain(500, numbers);
        var explicitSelection = new QuerySelection(keys: [items[5].FullPath, items[900].FullPath]);
        numbers.Clear();
        await foreach (var row in query.EnumerateSelectionAsync(explicitSelection)) numbers.Add((int)row.Values["number"]!);
        Assert.Equal(new[] { 900, 5 }, numbers);
    }

    [Fact]
    public async Task ColumnRefreshIsAtomicPreservesFailuresAndNeverTouchesOtherFields()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("name"), Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
        var snapshot = Publish(store, workspace.Scope, fields,
            workspace.Item("first", new() { ["name"] = "stable", ["text"] = "old first" }),
            workspace.Item("second", new() { ["name"] = "stable", ["text"] = "old second" }));
        var refreshed = await store.RefreshColumnAsync(snapshot, "text", (path, _) => Task.FromResult(
            path.EndsWith("first", StringComparison.Ordinal)
                ? new TextColumnReadResult("new first", [])
                : new TextColumnReadResult(null, [new ProfileMappingIssue("read_error", "text", "Access denied")])));
        await using var query = store.OpenQuery(refreshed, new());
        var rows = (await query.ReadRangeAsync(0, 500)).Items;
        Assert.Equal("new first", rows[0].Values["text"]);
        Assert.Equal("old second", rows[1].Values["text"]);
        Assert.All(rows, row => Assert.Equal("stable", row.Values["name"]));
        Assert.Equal("Access denied", rows[1].FieldErrors!["text"]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshColumnAsync(refreshed, "text", (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new TextColumnReadResult("must not publish", []));
        }, cancellationToken: cancellation.Token));
        Assert.Equal(refreshed.Generation, (await store.FindAsync(workspace.Scope))!.Generation);
        Assert.Equal("new first", (await query.ReadRangeAsync(0, 500)).Items[0].Values["text"]);
    }

    [Fact]
    public async Task CancelledColumnRefreshRemovesAlreadyFlushedStagingWithoutChangingPublication()
    {
        using var workspace = new Workspace();
        using var store = new SqliteCatalogProjectionStore(workspace.Database);
        var fields = new[] { Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
        var items = Enumerable.Range(0, 1005).Select(index => workspace.Item($"row-{index:D4}", new() { ["text"] = "original" })).ToArray();
        var original = Publish(store, workspace.Scope, fields, items);
        using var cancellation = new CancellationTokenSource();
        var visited = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshColumnAsync(original, "text", (_, _) =>
        {
            if (++visited == 700) cancellation.Cancel();
            return Task.FromResult(new TextColumnReadResult("refreshed", []));
        }, cancellationToken: cancellation.Token));
        Assert.Equal(700, visited);
        Assert.Equal(original.Generation, (await store.FindAsync(workspace.Scope))!.Generation);
        await using var query = store.OpenQuery(original, new());
        Assert.Equal(1005, await query.CountAsync());
        Assert.All((await query.ReadRangeAsync(0, 500)).Items, row => Assert.Equal("original", row.Values["text"]));
    }

    private static ProfileFieldDescriptor Field(string id, ProfileFieldValueKind kind = ProfileFieldValueKind.String) =>
        new(id, id, id, 0, false, kind, true, null, null);
    private static CatalogProjectionSummary Summary(int count) => new(count, count, 0, 0, 0, false, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
    private static CatalogProjectionSnapshot Publish(SqliteCatalogProjectionStore store,
        CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields, params CatalogItem[] items)
    {
        using var writer = store.Begin(scope, fields);
        foreach (var item in items) writer.Append(item);
        return writer.Publish(Summary(items.Length));
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "findeverything-projection-" + Guid.NewGuid().ToString("N"));
        public Workspace()
        {
            Directory.CreateDirectory(_directory);
            Scope = new("test-scope", "test-profile", _directory, Path.Combine(_directory, "engine.db"), "fingerprint", "en-US", Guid.NewGuid());
        }
        public string Database => Path.Combine(_directory, "catalog.db");
        public CatalogProjectionScope Scope { get; }
        public CatalogItem Item(string name, Dictionary<string, object?> values) =>
            new(Path.Combine(_directory, name), name, "rule", values, values, false);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
