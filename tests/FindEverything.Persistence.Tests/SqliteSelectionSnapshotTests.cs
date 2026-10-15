using System.Runtime.CompilerServices;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class SqliteSelectionSnapshotTests
{
    [Fact]
    public async Task Providers_can_preserve_keys_without_materializing_large_column_payloads()
    {
        await using var query = new KeyOnlyQuery();
        using var all = new QuerySelection(true);
        using var snapshot = await SqliteSelectionSnapshot.CreateAsync(query, all, value => value, value => value);
        Assert.True(snapshot.Contains("stable"));
        Assert.True(snapshot.ContainsQueryKey("effective"));
    }
    [Fact]
    public async Task All_matching_is_frozen_without_including_rows_from_a_later_filter()
    {
        await using var first = new KeysQuery(Enumerable.Range(0, 2500).Select(i => "A-" + i));
        using var selected = new QuerySelection(true);
        selected.Set("A-3", false);
        using var snapshot = await SqliteSelectionSnapshot.CreateAsync(first, selected, key => key, key => key);
        selected.ReplaceBaseline(snapshot);
        Assert.False(selected.AllMatching);
        Assert.Equal(2499, selected.Count(long.MaxValue));
        Assert.True(selected.Contains("a-2000"));
        Assert.False(selected.Contains("A-3"));
        Assert.False(selected.Contains("B-1"));
        await using var second = new KeysQuery(["A-1", "B-1"]);
        Assert.Equal(1, await ((IResultQuerySession<string>)second).CountSelectionAsync(selected));
    }

    [Theory]
    [InlineData(SqliteSelectionSnapshot.SetOperation.Union, new[] { "A", "B", "C", "D" })]
    [InlineData(SqliteSelectionSnapshot.SetOperation.Except, new[] { "A" })]
    [InlineData(SqliteSelectionSnapshot.SetOperation.SymmetricDifference, new[] { "A", "D" })]
    public async Task Current_result_bulk_actions_keep_other_searches(SqliteSelectionSnapshot.SetOperation operation, string[] expected)
    {
        await using var original = new KeysQuery(["A", "B"]);
        using var all = new QuerySelection(true);
        using var initial = await SqliteSelectionSnapshot.CreateAsync(original, all, key => key, key => key);
        using var previous = new QuerySelection(); previous.ReplaceBaseline(initial); previous.Set("C", true);
        await using var current = new KeysQuery(["B", "C", "D"]);
        using var currentKeys = await SqliteSelectionSnapshot.CreateAsync(current, all, key => key, key => key);
        using var merged = await SqliteSelectionSnapshot.CombineAsync(previous, currentKeys, operation);
        Assert.Equal(expected.Length, merged.Count);
        Assert.Equal(expected, new[] { "A", "B", "C", "D" }.Where(merged.Contains));
        Assert.Equal(3, previous.Count(long.MaxValue));
    }

    [Fact]
    public async Task Snapshot_lifetime_is_retained_until_the_last_selection_is_released()
    {
        await using var query = new KeysQuery(["A"]);
        using var all = new QuerySelection(true);
        var baseline = await SqliteSelectionSnapshot.CreateAsync(query, all, key => key, key => key);
        var path = baseline.DatabasePath;
        var selection = new QuerySelection(); selection.ReplaceBaseline(baseline);
        baseline.Dispose();
        Assert.True(File.Exists(path));
        selection.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Cancelled_snapshot_does_not_replace_existing_selection()
    {
        using var existing = new QuerySelection(keys: ["keep"]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var query = new KeysQuery(["replace"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteSelectionSnapshot.CreateAsync(query, existing, key => key, key => key, cancellation.Token));
        Assert.True(existing.Contains("keep"));
    }

    [Fact]
    public async Task Cancellation_after_streaming_started_keeps_the_previous_baseline_alive()
    {
        await using var original = new KeysQuery(["keep"]);
        using var all = new QuerySelection(true);
        using var baseline = await SqliteSelectionSnapshot.CreateAsync(original, all, key => key, key => key);
        using var existing = new QuerySelection(); existing.ReplaceBaseline(baseline);
        using var cancellation = new CancellationTokenSource();
        await using var interrupted = new InterruptedQuery(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqliteSelectionSnapshot.CreateAsync(interrupted, all, key => key, key => key, cancellation.Token));
        Assert.True(interrupted.EnumerationDisposed);
        Assert.True(File.Exists(baseline.DatabasePath));
        Assert.Equal(1, existing.Count(long.MaxValue));
        Assert.True(existing.Contains("keep"));
        Assert.False(existing.Contains("partial"));
    }

    private sealed class KeysQuery(IEnumerable<string> source) : IResultQuerySession<string>
    {
        private readonly string[] _keys = source.ToArray();
        public string Identity => "fixture";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)_keys.Length);
        public Task<ResultPage<string>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new ResultPage<string>(_keys.Skip(offset).Take(limit).ToArray(), offset, offset + limit < _keys.Length));
        public async IAsyncEnumerable<string> EnumerateSelectionAsync(QuerySelection selection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var key in _keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Contains(key)) yield return key;
            }
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InterruptedQuery(CancellationTokenSource cancellation) : IResultQuerySession<string>
    {
        public bool EnumerationDisposed { get; private set; }
        public string Identity => "cancelled-stream";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(2L);
        public Task<ResultPage<string>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return "partial";
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { EnumerationDisposed = true; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class KeyOnlyQuery : IResultQuerySession<string>, IResultSelectionKeyReader
    {
        public string Identity => "key-only";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<ResultPage<string>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Row payloads must not be read.");
        public IAsyncEnumerable<string> EnumerateSelectionAsync(QuerySelection selection, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Row payloads must not be read.");
        public async IAsyncEnumerable<ResultSelectionKey> EnumerateSelectionKeysAsync(QuerySelection selection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new("stable", "effective");
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
