using System.Reflection;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class CatalogOrderedPagingTests
{
    [Theory]
    [InlineData(ProfileFieldValueKind.Int32, false)]
    [InlineData(ProfileFieldValueKind.Int32, true)]
    [InlineData(ProfileFieldValueKind.Decimal, false)]
    [InlineData(ProfileFieldValueKind.DateTime, true)]
    [InlineData(ProfileFieldValueKind.String, false)]
    [InlineData(ProfileFieldValueKind.String, true)]
    public async Task Deep_sorted_pages_seek_a_single_file_backed_order_with_nulls_last(ProfileFieldValueKind kind, bool descending)
    {
        using var fixture = new Fixture();
        var rows = Enumerable.Range(0, 1600).Select(index => fixture.Row(index,
            index % 19 == 0 ? null : Value(kind, index % 97))).ToArray();
        var snapshot = fixture.Publish(Field(kind), rows);
        await using var query = fixture.Store.OpenQuery(snapshot, new(SortFieldId: "value", Descending: descending));
        var expected = rows.OrderBy(row => row.Values["value"] is null)
            .ThenBy(row => row.Values["value"], new ValueComparer(descending))
            .ThenBy(row => row.FullPath.ToUpperInvariant(), StringComparer.Ordinal).Select(row => row.FullPath).ToArray();
        Assert.Equal(expected.Take(30), (await query.ReadRangeAsync(0, 30)).Items.Select(row => row.FullPath));
        Assert.Equal(expected.Skip(1490).Take(100), (await query.ReadRangeAsync(1490, 100)).Items.Select(row => row.FullPath));
        Assert.Equal(expected.Skip(1590), (await query.ReadRangeAsync(1590, 30)).Items.Select(row => row.FullPath));
        Assert.Equal(1, BuildCount(query));
        var connection = CachedConnection(query, "_orderedPages");
        using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA temp_store;";
        Assert.Equal(1L, pragma.ExecuteScalar());
        pragma.CommandText = "PRAGMA temp.cache_size;"; Assert.Equal(-2048L, pragma.ExecuteScalar());
        using var transaction = connection.BeginTransaction(deferred: true);
        var builder = query.GetType().GetMethod("BuildOrderedPageCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var command = (SqliteCommand)builder.Invoke(query, [connection, transaction, 1490, 100])!;
        command.CommandText = "EXPLAIN QUERY PLAN SELECT r.full_path " + command.CommandText;
        using var reader = command.ExecuteReader();
        var plan = new List<string>(); while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("INTEGER PRIMARY KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selected_pages_reuse_the_order_until_explicit_selection_changes()
    {
        using var fixture = new Fixture();
        var rows = Enumerable.Range(0, 1600).Select(index => fixture.Row(index, index)).ToArray();
        var snapshot = fixture.Publish(Field(ProfileFieldValueKind.Int32), rows);
        await using var original = fixture.Store.OpenQuery(snapshot, new());
        using var all = new QuerySelection(true);
        using var keys = await SqliteSelectionSnapshot.CreateAsync(original, all, row => row.FullPath, row => row.FullPath);
        using var selection = new QuerySelection(); selection.ReplaceBaseline(keys);
        selection.Set(rows[1599].FullPath, false);
        selection.RemoveUnavailableKey(rows[1598].FullPath);
        await using var query = fixture.Store.OpenQuery(snapshot, new(SortFieldId: "value", Descending: true));
        Assert.Equal([1597, 1596], (await query.ReadSelectionRangeAsync(selection, 0, 2)).Items.Select(row => row.Values["value"]));
        Assert.Equal([1, 0], (await query.ReadSelectionRangeAsync(selection, 1596, 20)).Items.Select(row => row.Values["value"]));
        Assert.Equal(1, BuildCount(query));
        selection.Set(rows[1597].FullPath, false);
        Assert.Equal(1596, Assert.Single((await query.ReadSelectionRangeAsync(selection, 0, 1)).Items).Values["value"]);
        Assert.Equal(2, BuildCount(query));
    }

    [Fact]
    public async Task Full_selected_export_materializes_order_once_and_preserves_mapping_filter_and_saved_notes()
    {
        using var fixture = new Fixture();
        var value = Field(ProfileFieldValueKind.String) with
        { ValueMappings = new Dictionary<string, string> { ["A"] = "Zulu", ["B"] = "Alpha" } };
        var memo = new ProfileFieldDescriptor("memo", "memo", "Memo", 1, false, ProfileFieldValueKind.String, true, null, null)
        { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default" };
        var rows = Enumerable.Range(0, 1600).Select(index => fixture.Row(index, index % 2 == 0 ? "A" : "B")).ToArray();
        var snapshot = fixture.Publish([value, memo], rows);
        await fixture.Store.SetValueAsync(fixture.Scope.ProfileId, rows[1500].FullPath, "memo", "saved note");
        await using var query = fixture.Store.OpenQuery(snapshot, new(SortFieldId: "value"));
        using var selected = new QuerySelection(true); selected.Set(rows[1].FullPath, false);
        var exported = new List<CatalogProjectionRow>();
        await foreach (var row in query.EnumerateSelectionAsync(selected)) exported.Add(row);
        var expected = rows.Where(row => row.FullPath != rows[1].FullPath)
            .OrderBy(row => (string)row.Values["value"]! == "B" ? 0 : 1)
            .ThenBy(row => row.FullPath.ToUpperInvariant(), StringComparer.Ordinal).Select(row => row.FullPath);
        Assert.Equal(expected, exported.Select(row => row.FullPath));
        Assert.Equal("saved note", exported.Single(row => row.FullPath == rows[1500].FullPath).Values["memo"]);
        Assert.Equal(1, BuildCount(query));
        await using var filtered = fixture.Store.OpenQuery(snapshot, new("Alpha", SortFieldId: "value", ShowOriginalValues: false));
        Assert.Equal(800, await filtered.CountAsync());
        Assert.All((await filtered.ReadRangeAsync(700, 100)).Items, row => Assert.Equal("B", row.Values["value"]));
    }

    [Fact]
    public async Task Cached_note_sort_is_invalidated_and_cancelled_reads_can_be_retried()
    {
        using var fixture = new Fixture();
        var field = Field(ProfileFieldValueKind.String) with { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "default" };
        var rows = Enumerable.Range(0, 20).Select(index => fixture.Row(index, null)).ToArray();
        var snapshot = fixture.Publish(field, rows);
        await fixture.Store.SetValueAsync(fixture.Scope.ProfileId, rows[5].FullPath, "value", "Alpha");
        await using var query = fixture.Store.OpenQuery(snapshot, new(SortFieldId: "value"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ReadRangeAsync(0, 10, cancelled.Token));
        Assert.Equal(0, BuildCount(query));
        Assert.Equal("Alpha", (await query.ReadRangeAsync(0, 10)).Items[0].Values["value"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ReadRangeAsync(10, 10, cancelled.Token));
        Assert.Equal(10, (await query.ReadRangeAsync(10, 10)).Items.Count);
        await fixture.Store.SetValueAsync(fixture.Scope.ProfileId, rows[5].FullPath, "value", "Zulu");
        await Assert.ThrowsAsync<ResultQueryChangedException>(() => query.ReadRangeAsync(10, 10));
    }

    [Fact]
    public async Task Read_only_catalog_can_sort_using_private_temp_storage_and_disposal_closes_it()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Publish(Field(ProfileFieldValueKind.Int32), fixture.Row(0, 2), fixture.Row(1, 1));
        using var readOnly = new SqliteCatalogProjectionStore(fixture.Store.DatabasePath, readOnly: true);
        var query = readOnly.OpenQuery(snapshot, new(SortFieldId: "value"));
        Assert.Equal(1, (await query.ReadRangeAsync(0, 10)).Items[0].Values["value"]);
        var connection = CachedConnection(query, "_orderedPages");
        await query.DisposeAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static long BuildCount(object query) => (long)query.GetType().GetField("_orderedKeyBuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(query)!;
    private static SqliteConnection CachedConnection(object query, string name)
    {
        var cache = query.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(query)!;
        return (SqliteConnection)cache.GetType().GetField("Connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    }
    private static object Value(ProfileFieldValueKind kind, int value) => kind switch
    {
        ProfileFieldValueKind.Int32 => value,
        ProfileFieldValueKind.Decimal => (decimal)value / 7,
        ProfileFieldValueKind.DateTime => new DateTime(2026, 1, 1).AddDays(value),
        _ => $"text-{value:D3}",
    };
    private sealed class ValueComparer(bool descending) : IComparer<object?>
    {
        public int Compare(object? x, object? y) => (descending ? -1 : 1) * Comparer<object?>.Default.Compare(x, y);
    }
    private static ProfileFieldDescriptor Field(ProfileFieldValueKind kind) => new("value", "value", "Value", 0, false, kind, true, null, null);
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "findeverything-order-" + Guid.NewGuid().ToString("N"));
        internal SqliteCatalogProjectionStore Store { get; }
        internal CatalogProjectionScope Scope { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory); Store = new(Path.Combine(_directory, "catalog.db"));
            Scope = new("ordered", "ordered", _directory, Path.Combine(_directory, "metadata.db"), "ordered", "en-US", null);
        }
        internal CatalogItem Row(int index, object? value)
        { var values = new Dictionary<string, object?> { ["value"] = value }; return new(Path.Combine(_directory, $"row-{index:D4}"), $"row-{index:D4}", "rule", values, values, false); }
        internal CatalogProjectionSnapshot Publish(ProfileFieldDescriptor field, params CatalogItem[] rows) => Publish([field], rows);
        internal CatalogProjectionSnapshot Publish(ProfileFieldDescriptor[] fields, params CatalogItem[] rows)
        {
            using var writer = Store.Begin(Scope, fields); foreach (var row in rows) writer.Append(row);
            return writer.Publish(new(rows.Length, rows.Length, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
        }
        public void Dispose() { Store.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
