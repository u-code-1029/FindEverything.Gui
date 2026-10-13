using FindEverything.Application.Results;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class QuerySelectionBaselineTests
{
    [Fact]
    public void BaselineOverridesInvertAndCaptureAreIndependentAndBounded()
    {
        using var baseline = new Keys(new[] { "A", "B" }, new[] { "X", "Y" });
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(baseline);
        Assert.Equal(2, selection.Count(100_000)); Assert.Empty(selection.Keys);
        Assert.True(selection.Contains("a")); Assert.False(selection.Contains("C"));
        selection.Set("A", false); selection.Set("C", true);
        Assert.Equal(2, selection.Count(100_000));
        using var captured = selection.Capture();
        selection.Invert(); Assert.Equal(99_998, selection.Count(100_000));
        Assert.True(selection.Contains("A")); Assert.False(selection.Contains("C"));
        Assert.False(captured.Contains("A")); Assert.True(captured.Contains("B")); Assert.True(captured.Contains("C"));
        captured.Set("A", true); captured.Set("C", false); Assert.Equal(2, captured.Count(100_000)); Assert.Empty(captured.Keys);
        selection.SelectAll(); Assert.Null(selection.Baseline); Assert.Equal(100_000, selection.Count(100_000));
        selection.Clear(); Assert.Equal(0, selection.Count(100_000));
    }

    [Fact]
    public void ImportedOverrideTranslationKeepsBaselineQueryMembership()
    {
        using var baseline = new Keys(new[] { "A", "B" }, new[] { "X", "Y" });
        using var original = new QuerySelection(); original.ReplaceBaseline(baseline); original.Set("A", false);
        using var mapped = original.MapKeys(value => value == "A" ? "X" : value);
        Assert.False(mapped.Contains("X")); Assert.True(mapped.Contains("Y")); Assert.Equal(1, mapped.Count(10));
        Assert.Equal("private.db", mapped.Baseline!.DatabasePath);
    }

    [Fact]
    public void EditedRowLeavingQueryAdjustsBaselineAndInvertedCountsWithoutClearingOtherChecks()
    {
        using var baseline = new Keys(new[] { "A", "B" }, new[] { "X", "Y" });
        using var selected = new QuerySelection(); selected.ReplaceBaseline(baseline);
        selected.RemoveUnavailableKey("A"); Assert.Equal(1, selected.Count(9)); Assert.False(selected.Contains("A")); Assert.True(selected.Contains("B"));
        selected.Invert(); Assert.Equal(8, selected.Count(9)); Assert.False(selected.Contains("A")); Assert.True(selected.Contains("C"));
        selected.RemoveUnavailableKey("A"); Assert.Equal(8, selected.Count(9));
        using var mapped = selected.MapKeys(value => value == "A" ? "X" : value == "B" ? "Y" : value);
        Assert.Contains("X", mapped.UnavailableKeys);
        selected.Set("A", true); Assert.True(selected.Contains("A")); Assert.Empty(selected.UnavailableKeys);
    }

    private sealed class Keys(HashSet<string> stable, HashSet<string> query) : IQuerySelectionKeySet
    {
        public Keys(string[] stable, string[] query) : this(new HashSet<string>(stable, StringComparer.OrdinalIgnoreCase), new HashSet<string>(query, StringComparer.OrdinalIgnoreCase)) { }
        public long Count => stable.Count;
        public string DatabasePath => "private.db";
        public bool Contains(string key) => stable.Contains(key);
        public bool ContainsQueryKey(string key) => query.Contains(key);
        public IQuerySelectionKeySet Retain() => new Keys(stable, query);
        public void Dispose() { }
    }
}
