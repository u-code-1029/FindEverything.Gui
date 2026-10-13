using System.Diagnostics;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class CatalogColumnFilterTests
{
    [Fact]
    public async Task MultipleTypedPredicatesUseExactDecimalsInclusiveEndDateAndSameOutput()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("amount", ProfileFieldValueKind.Decimal), Field("date", ProfileFieldValueKind.DateTime), Field("name"), Field("flag", ProfileFieldValueKind.Boolean) };
        var tiny = 0.0000000000000000000000000001m;
        var day = new DateTime(2026, 5, 21);
        var snapshot = workspace.Publish(fields,
            Values("inside", tiny, day.AddDays(1).AddTicks(-1), "Apollo", true),
            Values("too-large", tiny * 2, day, "Apollo", true),
            Values("next-day", tiny, day.AddDays(1), "Apollo", true),
            Values("excluded", tiny, day, "Apollo skip", true),
            Values("false", tiny, day, "Apollo", false));
        var filters = new[]
        {
            new CatalogColumnFilter(CatalogColumnTarget.Field("amount"), CatalogFilterOperator.NumberRange) { Minimum = tiny, Maximum = tiny },
            new CatalogColumnFilter(CatalogColumnTarget.Field("date"), CatalogFilterOperator.DateRange) { StartDate = day, EndDate = day },
            new CatalogColumnFilter(CatalogColumnTarget.Field("name"), CatalogFilterOperator.DoesNotContain) { Text = "skip" },
            new CatalogColumnFilter(CatalogColumnTarget.Field("flag"), CatalogFilterOperator.BooleanEquals) { Boolean = true }
        };
        await using var query = workspace.Store.OpenQuery(snapshot, new("APOLLO") { Filters = filters });
        Assert.Equal(1, await query.CountAsync());
        Assert.EndsWith("inside", Assert.Single((await query.ReadRangeAsync(0, 500)).Items).FullPath);
        var output = new List<CatalogProjectionRow>();
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true))) output.Add(row);
        Assert.EndsWith("inside", Assert.Single(output).FullPath);
        var range = await workspace.Store.ReadNumericRangeAsync(snapshot, "amount");
        Assert.Equal(new CatalogNumericRange(tiny, tiny * 2), range);
    }

    [Fact]
    public async Task ColumnTextFilteringUsesFullTextNotDisplayPreview()
    {
        using var workspace = new Workspace();
        var snapshot = workspace.Publish([Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent }],
            ("tail", new() { ["text"] = new string('a', 6000) + "TAIL_ONLY" }));
        await using var query = workspace.Store.OpenQuery(snapshot, new() { TextPreviewCharacters = 2048,
            Filters = [new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.Contains) { Text = "TAIL_ONLY" }] });
        Assert.Equal(1, await query.CountAsync());
        Assert.Equal(2048, ((string)Assert.Single((await query.ReadRangeAsync(0, 500)).Items).Values["text"]!).Length);
        await foreach (var row in query.EnumerateSelectionAsync(new QuerySelection(true))) Assert.Contains("TAIL_ONLY", (string)row.Values["text"]!);
    }

    [Fact]
    public async Task LiveStageMatchesBoundedPathsUsingFullValuesAndRejectsResetRevisions()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("text") with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
        using var writer = workspace.Store.Begin(workspace.Scope, fields);
        var full = Path.Combine(workspace.Directory, "live");
        var values = new Dictionary<string, object?> { ["text"] = new string('a', 6000) + "TAIL_ONLY" };
        writer.Append(new CatalogItem(full, "live", "rule", values, values, false)); writer.Flush();
        CatalogProjectionSnapshot Stage() => new(writer.Generation, workspace.Scope,
            new(writer.Count, writer.Count, 0, 0, 0, true, null, DateTimeOffset.UtcNow), fields)
            { IsBuilding = true, BuildingRevision = writer.Revision };
        var stage = Stage();
        var query = new CatalogProjectionQuery("TAIL_ONLY") { TextPreviewCharacters = 2048,
            Filters = [new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.Contains) { Text = "TAIL_ONLY" }] };
        var paths = await workspace.Store.FindMatchingPathsAsync(stage, query, [full, Path.Combine(workspace.Directory, "absent")]);
        Assert.Equal(full, Assert.Single(paths!));
        writer.Reset();
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => workspace.Store.FindMatchingPathsAsync(stage, query, [full]));
        writer.Append(new CatalogItem(full, "live", "rule", values, values, false)); writer.Flush();
        Assert.Equal(full, Assert.Single((await workspace.Store.FindMatchingPathsAsync(Stage(), query, [full]))!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.Store.FindMatchingPathsAsync(Stage(), query,
            Enumerable.Repeat(full, 501).ToArray()));
    }

    [Fact]
    public void SliderInterpolationHandlesDecimalExtremesAndNearEqualLargeBounds()
    {
        var full = new CatalogNumericRange(decimal.MinValue, decimal.MaxValue);
        Assert.Equal(0m, CatalogNumericRangeMath.Interpolate(full, 5000));
        Assert.Equal(decimal.MinValue, CatalogNumericRangeMath.Interpolate(full, 0));
        Assert.Equal(decimal.MaxValue, CatalogNumericRangeMath.Interpolate(full, 10000));
        var near = new CatalogNumericRange(decimal.MaxValue - 2, decimal.MaxValue);
        Assert.Equal(0.5, CatalogNumericRangeMath.Position(near, decimal.MaxValue - 1));
        Assert.Equal(decimal.MaxValue - 1, CatalogNumericRangeMath.Interpolate(near, 5000));
        Assert.Equal(decimal.MaxValue, CatalogNumericRangeMath.Interpolate(new(decimal.MaxValue, decimal.MaxValue), 5000));
    }

    [Fact]
    public async Task DecimalExtremesAndNegativeFractionalBoundsAreNotRounded()
    {
        using var workspace = new Workspace();
        var values = new[] { decimal.MinValue, -0.0000000000000000000000000002m, -0.0000000000000000000000000001m, 0m, decimal.MaxValue };
        var snapshot = workspace.Publish([Field("amount", ProfileFieldValueKind.Decimal)],
            values.Select((value, index) => (index.ToString(), new Dictionary<string, object?> { ["amount"] = value })).ToArray());
        await using var query = workspace.Store.OpenQuery(snapshot, new() { Filters = [new(CatalogColumnTarget.Field("amount"), CatalogFilterOperator.NumberRange)
            { Minimum = values[1], Maximum = values[2] }], SortTarget = CatalogColumnTarget.Field("amount") });
        Assert.Equal(2, await query.CountAsync());
        Assert.Equal(values[1..3], (await query.ReadRangeAsync(0, 500)).Items.Select(row => (decimal)row.Values["amount"]!).ToArray());
        Assert.Equal(new CatalogNumericRange(decimal.MinValue, decimal.MaxValue), await workspace.Store.ReadNumericRangeAsync(snapshot, "amount"));
    }

    [Fact]
    public async Task MissingEmptyAndDashAreDistinctAndMappedOrCurrentNoteValuesAreUsed()
    {
        using var workspace = new Workspace();
        var fields = new[] { Field("text"), Field("alias") with { ValueMappings = new Dictionary<string, string> { ["A"] = "Alice" } },
            Field("memo") with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default" } };
        var snapshot = workspace.Publish(fields,
            ("null", new() { ["text"] = null, ["alias"] = "A" }),
            ("empty", new() { ["text"] = "", ["alias"] = "A" }),
            ("dash", new() { ["text"] = "—", ["alias"] = "A" }));
        async Task<string[]> Paths(CatalogColumnFilter filter, bool original = false)
        {
            await using var query = workspace.Store.OpenQuery(snapshot, new(ShowOriginalValues: original) { Filters = [filter] });
            return (await query.ReadRangeAsync(0, 500)).Items.Select(row => row.RelativePath).ToArray();
        }
        Assert.Equal(["null"], await Paths(new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.IsMissing)));
        Assert.Equal(["empty"], await Paths(new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.IsEmpty)));
        Assert.Equal(["empty"], await Paths(new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.DoesNotContain) { Text = "—" }));
        Assert.Equal(3, (await Paths(new(CatalogColumnTarget.Field("alias"), CatalogFilterOperator.Contains) { Text = "ALICE" })).Length);
        Assert.Empty(await Paths(new(CatalogColumnTarget.Field("alias"), CatalogFilterOperator.Contains) { Text = "Alice" }, original: true));
        await workspace.Store.SetValueAsync(snapshot.Scope.ProfileId, Path.Combine(workspace.Directory, "dash"), "memo", "changed");
        Assert.Equal(["dash"], await Paths(new(CatalogColumnTarget.Field("memo"), CatalogFilterOperator.Contains) { Text = "changed" }));
        Assert.Equal(2, (await Paths(new(CatalogColumnTarget.Field("memo"), CatalogFilterOperator.Contains) { Text = "default" })).Length);
    }

    [Fact]
    public async Task ExplicitTargetDoesNotConfuseProfileFullPathFieldWithBuiltinPath()
    {
        using var workspace = new Workspace();
        var snapshot = workspace.Publish([Field("fullpath")], ("aaa", new() { ["fullpath"] = "z" }), ("zzz", new() { ["fullpath"] = "a" }));
        await using var query = workspace.Store.OpenQuery(snapshot, new() { SortTarget = CatalogColumnTarget.Field("FullPath"),
            Filters = [new(CatalogColumnTarget.Field("FullPath"), CatalogFilterOperator.Contains) { Text = "a" }] });
        Assert.EndsWith("zzz", Assert.Single((await query.ReadRangeAsync(0, 500)).Items).FullPath);
        await using var path = workspace.Store.OpenQuery(snapshot, new() { SortTarget = CatalogColumnTarget.FullPath });
        Assert.EndsWith("aaa", (await path.ReadRangeAsync(0, 500)).Items[0].FullPath);
    }

    [Fact]
    public void InvalidTypedConditionsAreRejectedAndLivePredicatesMatchNullAndDates()
    {
        var fields = new[] { Field("count", ProfileFieldValueKind.Int32), Field("date", ProfileFieldValueKind.DateTime) };
        Assert.Throws<ArgumentException>(() => CatalogColumnFilters.Validate(new(CatalogColumnTarget.Field("count"), CatalogFilterOperator.NumberRange) { Minimum = 0.5m }, fields));
        Assert.Throws<ArgumentException>(() => CatalogColumnFilters.Validate(new(CatalogColumnTarget.Field("count"), CatalogFilterOperator.Contains) { Text = "1" }, fields));
        Assert.Throws<ArgumentException>(() => CatalogColumnFilters.Validate(new(CatalogColumnTarget.Field("date"), CatalogFilterOperator.DateRange) { StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(-1) }, fields));
        Assert.False(CatalogColumnFilters.Matches(new(CatalogColumnTarget.Field("missing"), CatalogFilterOperator.DoesNotContain) { Text = "x" }, null, "—"));
        Assert.True(CatalogColumnFilters.Matches(new(CatalogColumnTarget.Field("date"), CatalogFilterOperator.DateRange) { EndDate = new DateTime(2026, 5, 21) }, new DateTime(2026, 5, 21, 23, 59, 59), ""));
    }

    [Fact]
    public async Task NativeCancellationInterruptsExecutingSqlRatherThanOnlyCancellingAwait()
    {
        using var token = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var running = Task.Run(() => SqliteReadCancellation.RunAsync(async cancellation =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=false;Default Timeout=1"); connection.Open();
            using var interrupt = new SqliteReadCancellation(connection, cancellation);
            using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE counter(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM counter WHERE x<100000000) SELECT SUM(x) FROM counter;";
            started.SetResult();
            return await command.ExecuteScalarAsync(cancellation);
        }, token.Token));
        await started.Task;
        token.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BusyReadCancellationHasBoundedNativeWaitAndDoesNotCancelWriter()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Directory, "locked.db");
        using var blocker = new SqliteConnection($"Data Source={path};Pooling=false"); blocker.Open();
        using var hold = blocker.CreateCommand(); hold.CommandText = "CREATE TABLE sample(value); BEGIN EXCLUSIVE; INSERT INTO sample VALUES(42);"; hold.ExecuteNonQuery();
        using var token = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(() => SqliteReadCancellation.RunAsync(async cancellation =>
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false;Default Timeout=1"); connection.Open();
            using var interrupt = new SqliteReadCancellation(connection, cancellation);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sample;";
            started.TrySetResult(); return await command.ExecuteScalarAsync(cancellation);
        }, token.Token));
        await started.Task; var watch = Stopwatch.StartNew(); token.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        hold.CommandText = "COMMIT; SELECT value FROM sample;";
        Assert.Equal(42L, hold.ExecuteScalar());
    }

    private static (string Name, Dictionary<string, object?> Values) Values(string name, decimal amount, DateTime date, string text, bool flag) =>
        (name, new() { ["amount"] = amount, ["date"] = date, ["name"] = text, ["flag"] = flag });
    private static ProfileFieldDescriptor Field(string id, ProfileFieldValueKind kind = ProfileFieldValueKind.String) => new(id, id, id, 0, false, kind, true, null, null);
    private sealed class Workspace : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "findeverything-filters-" + Guid.NewGuid().ToString("N"));
        public SqliteCatalogProjectionStore Store { get; }
        private readonly CatalogProjectionScope _scope;
        public CatalogProjectionScope Scope => _scope;
        public Workspace()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Path.Combine(Directory, "catalog.db"));
            _scope = new("filter-test", "profile", Directory, Path.Combine(Directory, "metadata.db"), "fingerprint", "en-US", Guid.NewGuid());
        }
        public CatalogProjectionSnapshot Publish(IReadOnlyList<ProfileFieldDescriptor> fields, params (string Name, Dictionary<string, object?> Values)[] rows)
        {
            using var writer = Store.Begin(_scope, fields);
            foreach (var row in rows) writer.Append(new CatalogItem(Path.Combine(Directory, row.Name), row.Name, "rule", row.Values, row.Values, false));
            return writer.Publish(new(rows.Length, rows.Length, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
        }
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
}
