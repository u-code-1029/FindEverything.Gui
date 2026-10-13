using System.Collections.Concurrent;
using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class ResultResponsivenessTests
{
    [Fact]
    public void Progress_keeps_one_dispatch_and_flushes_only_the_latest_count()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var displayed = new List<string>();
        using var progress = new CoalescingProgressDelivery<string>(callbacks.Enqueue, displayed.Add, TimeSpan.FromHours(1));
        for (var index = 0; index < 100_000; index++) progress.Report(index.ToString());
        Assert.Empty(callbacks);
        progress.FlushAndClose();
        progress.FlushAndClose();
        Assert.Single(callbacks);
        Assert.True(callbacks.TryDequeue(out var callback));
        callback();
        Assert.Equal(["99999"], displayed);
        progress.Report("late");
        Assert.Empty(callbacks);
    }

    [Fact]
    public async Task Page_loader_never_calls_synchronous_storage_or_formats_rows_on_the_caller_thread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var storageThread = 0;
        var mappingThread = 0;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var query = new Query((_, _, _) =>
        {
            storageThread = Environment.CurrentManagedThreadId;
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Task.FromResult(new ResultPage<string>(["one"], 0, false));
        });
        var loader = new ResultPageLoader<string, string>(query, value =>
        {
            mappingThread = Environment.CurrentManagedThreadId;
            return value.ToUpperInvariant();
        });
        var loading = loader.ReadAsync(0, 500, CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        var result = await loading;
        Assert.NotEqual(caller, storageThread);
        Assert.NotEqual(caller, mappingThread);
        Assert.Equal("ONE", Assert.Single(result.Items));
    }

    [Fact]
    public async Task Cancelled_range_is_atomic_and_does_not_restore_checks_after_clear()
    {
        using var selection = new QuerySelection(false, ["previous"]);
        using var controller = new ResultSelectionController(selection);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ResultPage<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query((_, _, _) => { started.TrySetResult(); return release.Task; });
        var selecting = controller.SelectAsync(query, "end", 100, 0, true, true, value => value, () => true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["previous"], selection.Keys);
        controller.Cancel(); selection.Clear();
        release.SetResult(new(["first", "end"], 0, false));
        Assert.False(await selecting);
        Assert.Empty(selection.Keys);
    }

    [Fact]
    public async Task Query_replacement_rejects_a_late_range_and_preserves_the_new_selection()
    {
        using var selection = new QuerySelection(false, ["initial"]);
        using var controller = new ResultSelectionController(selection);
        var current = true;
        var query = new Query((_, _, _) =>
        {
            current = false;
            return Task.FromResult(new ResultPage<string>(["stale"], 0, false));
        });
        Assert.False(await controller.SelectAsync(query, "last", 1, 0, false, true, value => value, () => current));
        Assert.Equal(["initial"], selection.Keys);
    }

    [Fact]
    public async Task Baseline_page_membership_is_batched_and_inversion_overrides_are_preserved()
    {
        using var keys = new BatchedKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        selection.Set("A", false);
        selection.Set("C", true);
        selection.Invert();
        var before = keys.SyncReads;
        Assert.Equal([true, false, false, true], await selection.ReadMembershipAsync(["A", "B", "C", "D"]));
        Assert.Equal(1, keys.BatchReads);
        Assert.Equal(before, keys.SyncReads);
        using var restored = new QuerySelection(); restored.ReplaceWith(selection);
        Assert.Equal(2, restored.Count(4));
        Assert.Equal([true, false, false, true], await restored.ReadMembershipAsync(["A", "B", "C", "D"]));
    }

    [Fact]
    public async Task Additive_range_uses_one_baseline_batch_per_page_instead_of_one_read_per_row()
    {
        using var keys = new BatchedKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        using var controller = new ResultSelectionController(selection);
        var query = new Query((_, _, _) => Task.FromResult(new ResultPage<string>(["A", "B", "C"], 0, false)));
        Assert.True(await controller.SelectAsync(query, "C", 2, 0, true, true, value => value, () => true));
        Assert.Equal(0, keys.SyncReads);
        Assert.Equal(1, keys.BatchReads);
        Assert.Equal(3, selection.Count(4));
        Assert.Equal([true, true, true, false], await selection.ReadMembershipAsync(["A", "B", "C", "D"]));
    }

    [Fact]
    public async Task Cancelling_a_range_releases_its_captured_baseline_lease()
    {
        using var keys = new LeaseKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        using var controller = new ResultSelectionController(selection);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new ResultPage<string>([], 0, false);
        });
        var selecting = controller.SelectAsync(query, "C", 2, 0, true, true, value => value, () => true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, keys.Leases); // Owner, current selection, in-flight capture.
        controller.Cancel();
        Assert.False(await selecting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, keys.Leases);
        selection.Clear();
        Assert.Equal(1, keys.Leases);
        keys.Dispose();
        Assert.Equal(0, keys.Leases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rapid_baseline_toggles_preserve_every_click_even_when_reads_finish_out_of_order(bool sameRow, bool reverseCompletion)
    {
        using var keys = new DelayedBatchKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        using var controller = new ResultSelectionController(selection);
        var query = new Query((_, _, _) => throw new InvalidOperationException("A single click does not read a range."));
        var first = controller.SelectAsync(query, "A", 0, -1, true, false, value => value, () => true);
        var second = controller.SelectAsync(query, sameRow ? "A" : "B", 1, -1, true, false, value => value, () => true);
        Assert.Equal(2, keys.Reads.Count);
        keys.Complete(reverseCompletion ? 1 : 0);
        Assert.True(await (reverseCompletion ? second : first));
        keys.Complete(reverseCompletion ? 0 : 1);
        Assert.True(await (reverseCompletion ? first : second));
        Assert.Equal(sameRow ? [] : new[] { "A", "B" }, selection.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, selection.Count(2));
    }

    [Fact]
    public async Task Bulk_clear_cancels_pending_additive_toggles_without_restoring_the_old_selection()
    {
        using var keys = new DelayedBatchKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        using var controller = new ResultSelectionController(selection);
        var query = new Query((_, _, _) => throw new NotSupportedException());
        var first = controller.SelectAsync(query, "A", 0, -1, true, false, value => value, () => true);
        var second = controller.SelectAsync(query, "B", 1, -1, true, false, value => value, () => true);
        controller.Cancel(); selection.Clear(); selection.Set("new", true);
        keys.Complete(0); keys.Complete(1);
        Assert.False(await first);
        Assert.False(await second);
        Assert.Equal(["new"], selection.Keys);
    }

    [Fact]
    public async Task An_edited_row_leaving_the_query_uses_batch_membership_without_sync_reads()
    {
        using var keys = new BatchedKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        await selection.RemoveUnavailableKeyAsync("A");
        Assert.Equal(0, keys.SyncReads);
        Assert.Equal(1, keys.BatchReads);
        Assert.Equal(1, selection.Count(9));
        Assert.Equal([false, true], await selection.ReadMembershipAsync(["A", "B"]));
    }

    [Fact]
    public async Task Clearing_selection_during_an_edited_row_membership_read_never_restores_old_checks()
    {
        using var keys = new DeferredKeys();
        using var selection = new QuerySelection();
        selection.ReplaceBaseline(keys);
        var removing = selection.RemoveUnavailableKeyAsync("A");
        selection.Clear(); selection.Set("new", true);
        keys.Release.SetResult(new HashSet<string>(["A"], StringComparer.OrdinalIgnoreCase));
        await removing;
        Assert.Null(selection.Baseline);
        Assert.Equal(1, selection.Count(9));
        Assert.True(selection.Contains("new"));
        Assert.False(selection.Contains("A"));
    }

    private sealed class Query(Func<int, int, CancellationToken, Task<ResultPage<string>>> read) : IResultQuerySession<string>
    {
        public string Identity => "test";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(100L);
        public Task<ResultPage<string>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) => read(offset, limit, cancellationToken);
        public async IAsyncEnumerable<string> EnumerateSelectionAsync(QuerySelection selection,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BatchedKeys : IQuerySelectionKeySet
    {
        public int SyncReads { get; private set; }
        public int BatchReads { get; private set; }
        public long Count => 2;
        public string DatabasePath => "test";
        public bool Contains(string key) { SyncReads++; return key is "A" or "B"; }
        public Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
        {
            BatchReads++;
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(keys.Where(key => key is "A" or "B"), StringComparer.OrdinalIgnoreCase));
        }
        public IQuerySelectionKeySet Retain() => this;
        public void Dispose() { }
    }

    private sealed class DeferredKeys : IQuerySelectionKeySet
    {
        public TaskCompletionSource<IReadOnlySet<string>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Count => 1;
        public string DatabasePath => "test";
        public bool Contains(string key) => throw new InvalidOperationException("Synchronous storage read on the UI path.");
        public Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default) => Release.Task;
        public IQuerySelectionKeySet Retain() => this;
        public void Dispose() { }
    }

    private sealed class LeaseKeys : IQuerySelectionKeySet
    {
        private sealed class Shared { public int Leases = 1; }
        private readonly Shared _shared;
        private int _disposed;
        public LeaseKeys() => _shared = new Shared();
        private LeaseKeys(Shared shared) => _shared = shared;
        public int Leases => Volatile.Read(ref _shared.Leases);
        public long Count => 0;
        public string DatabasePath => "test";
        public bool Contains(string key) => false;
        public IQuerySelectionKeySet Retain()
        {
            Interlocked.Increment(ref _shared.Leases);
            return new LeaseKeys(_shared);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _shared.Leases);
        }
    }

    private sealed class DelayedBatchKeys : IQuerySelectionKeySet
    {
        public List<(IReadOnlyList<string> Keys, TaskCompletionSource<IReadOnlySet<string>> Completion)> Reads { get; } = [];
        public long Count => 1;
        public string DatabasePath => "test";
        public bool Contains(string key) => throw new InvalidOperationException("Synchronous membership must not run.");
        public Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IReadOnlySet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Reads.Add((keys, completion));
            return completion.Task;
        }
        public void Complete(int index) => Reads[index].Completion.SetResult(
            new HashSet<string>(Reads[index].Keys.Where(key => key == "A"), StringComparer.OrdinalIgnoreCase));
        public IQuerySelectionKeySet Retain() => this;
        public void Dispose() { }
    }
}
