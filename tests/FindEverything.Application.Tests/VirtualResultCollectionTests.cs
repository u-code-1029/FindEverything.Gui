using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class VirtualResultCollectionTests
{
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
            var value = await Task.Run(() => list[0]).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("placeholder", value);
            Assert.Equal(0, list.CachedItemCount);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
