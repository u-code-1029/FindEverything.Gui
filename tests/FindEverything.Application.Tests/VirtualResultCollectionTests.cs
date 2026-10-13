using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class VirtualResultCollectionTests
{
    [Fact]
    public async Task One_requested_slot_creates_one_placeholder_and_publishes_one_single_replace()
    {
        var ready = new TaskCompletionSource<ResultPage<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var placeholders = 0;
        var replacements = new ConcurrentQueue<NotifyCollectionChangedEventArgs>();
        using var list = new VirtualResultCollection<string>((_, _, token) => ready.Task.WaitAsync(token),
            index => { Interlocked.Increment(ref placeholders); return $"placeholder-{index}"; },
            action => action(), exception => loaded.TrySetException(exception));
        list.SetCount(1_000_000);
        list.CollectionChanged += (_, args) => replacements.Enqueue(args);
        list.PageLoaded += (_, page) =>
        {
            Assert.Equal(0, page.Offset);
            Assert.Equal(500, page.Items.Count);
            loaded.TrySetResult();
        };

        var first = list[42];
        Assert.Same(first, list[42]);
        Assert.Equal(42, list.IndexOf(first));
        Assert.Equal(1, placeholders);
        ready.SetResult(new(Enumerable.Range(0, 500).Select(index => $"row-{index}").ToArray(), 0, true));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var replacement = Assert.Single(replacements);
        Assert.Equal(NotifyCollectionChangedAction.Replace, replacement.Action);
        Assert.Equal(42, replacement.NewStartingIndex);
        Assert.Single(replacement.NewItems!);
        Assert.Single(replacement.OldItems!);
        Assert.Equal(-1, list.IndexOf(first));
        Assert.Equal(42, list.IndexOf(list[42]));
        Assert.Equal(1, placeholders);
    }

    [Fact]
    public void Loaded_sample_never_materializes_unrequested_virtual_rows()
    {
        var placeholderCount = 0;
        using var list = new VirtualResultCollection<string>((_, _, _) => throw new InvalidOperationException("No load expected"),
            index => { placeholderCount++; return $"placeholder-{index}"; }, action => action(), _ => { });
        list.Seed(new(Enumerable.Range(0, 500).Select(index => $"row-{index}").ToArray(), 0, true), 1_000_000);

        var sample = list.GetLoadedSample(200);
        Assert.Equal(200, sample.Count);
        Assert.Equal("row-0", sample[0]);
        Assert.Equal("row-199", sample[^1]);
        Assert.Equal(500, list.CachedItemCount);
        Assert.Equal(0, placeholderCount);
        Assert.Empty(list.GetLoadedSample(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetLoadedSample(-1));
    }

    [Fact]
    public void Trimming_and_eviction_remove_reverse_indices_and_keep_ready_rows()
    {
        using var list = new VirtualResultCollection<string>((_, _, _) => throw new InvalidOperationException("No load expected"),
            index => $"placeholder-{index}", action => action(), _ => { });
        var initial = Enumerable.Range(0, 500).Select(index => $"row-{index}").ToArray();
        list.Seed(new(initial, 0, true), 1_000_000);
        list.SetCount(100);
        Assert.Equal(100, list.CachedItemCount);
        Assert.Equal(99, list.IndexOf(initial[99]));
        Assert.Equal(-1, list.IndexOf(initial[100]));
        list.SetCount(1_000_000);
        for (var page = 1; page <= 10; page++)
            list.Seed(new(Enumerable.Range(page * 500, 500).Select(index => $"row-{index}").ToArray(), page * 500, true), 1_000_000);
        Assert.Equal(5000, list.CachedItemCount);
        Assert.Equal(-1, list.IndexOf(initial[99]));
    }

    [Fact]
    public async Task A_disposed_collection_does_not_publish_a_late_provider_result()
    {
        var ready = new TaskCompletionSource<ResultPage<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new ConcurrentQueue<Action>();
        var replacements = 0;
        var list = new VirtualResultCollection<string>((_, _, _) => { started.TrySetResult(); return ready.Task; },
            index => $"placeholder-{index}", action => callbacks.Enqueue(action), _ => { });
        list.SetCount(500);
        list.CollectionChanged += (_, _) => replacements++;
        _ = list[0];
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        list.Dispose();
        ready.SetResult(new(["late-row"], 0, false));
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (callbacks.IsEmpty && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(callbacks.TryDequeue(out var publish));
        publish();
        Assert.Equal(0, replacements);
        Assert.Equal(0, list.CachedItemCount);
    }

    [Fact]
    public async Task Synchronous_provider_work_does_not_block_indexer_and_cached_count_excludes_placeholders()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var list = new VirtualResultCollection<string>((offset, limit, token) =>
        {
            started.TrySetResult();
            release.Wait(token);
            return Task.FromResult(new ResultPage<string>(Enumerable.Range(offset, limit)
                .Select(index => $"row-{index}").ToArray(), offset, true));
        }, _ => "placeholder", action => action(), exception => loaded.TrySetException(exception));
        list.SetCount(1_000_000);
        list.PageLoaded += (_, _) => loaded.TrySetResult();
        try
        {
            // A dedicated caller separates a blocking-indexer regression from
            // thread-pool scheduling delays on a busy Windows CI machine.
            var value = await Task.Factory.StartNew(() => list[0], CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("placeholder", value);
            Assert.Equal(0, list.CachedItemCount);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            release.Set();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(500, list.CachedItemCount);
            Assert.Equal("row-0", list[0]);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Rapid_scrolling_a_million_rows_keeps_two_workers_and_five_thousand_cached_rows()
    {
        using var release = new SemaphoreSlim(0);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        using var list = new VirtualResultCollection<string>(async (offset, limit, token) =>
        {
            var running = Interlocked.Increment(ref active);
            if (running > 2) loaded.TrySetException(new InvalidOperationException("Worker limit exceeded."));
            maximumActive = Math.Max(maximumActive, running);
            try
            {
                await release.WaitAsync(token);
                return new ResultPage<string>(Enumerable.Range(offset, limit)
                    .Select(index => $"row-{index}").ToArray(), offset, true);
            }
            finally { Interlocked.Decrement(ref active); }
        }, _ => "placeholder", action => action(), exception => loaded.TrySetException(exception));
        list.SetCount(1_000_000);
        list.PageLoaded += (_, _) =>
        {
            if (list.CachedItemCount > 5000) loaded.TrySetException(new InvalidOperationException("Cache limit exceeded."));
            if (list.CachedItemCount == 5000) loaded.TrySetResult();
        };

        for (var index = 0; index < 100; index++) _ = list[index * 500];
        release.Release(32);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1_000_000, list.Count);
        Assert.InRange(maximumActive, 1, 2);
        Assert.Equal(5000, list.CachedItemCount);
        Assert.Equal(5000, list.LoadedItems.Count);
    }

    [Fact]
    public void Select_all_invert_and_capture_use_keys_without_enumerating_rows()
    {
        var selection = new QuerySelection();
        selection.SelectAll();
        Assert.Equal(1_000_000, selection.Count(1_000_000));
        Assert.Empty(selection.Keys);
        selection.Set("excluded-path", false);
        var captured = selection.Capture();
        selection.Invert();

        Assert.False(captured.Contains("excluded-path"));
        Assert.True(captured.Contains("any-unloaded-path"));
        Assert.Equal(999_999, captured.Count(1_000_000));
        Assert.True(selection.Contains("excluded-path"));
        Assert.Equal(1, selection.Count(1_000_000));
    }
}
