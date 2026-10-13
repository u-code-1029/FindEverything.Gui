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
}
