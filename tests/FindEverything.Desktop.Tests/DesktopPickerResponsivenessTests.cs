using System.Diagnostics;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class DesktopPickerResponsivenessTests
{
    [Fact]
    public async Task Slow_network_probe_times_out_without_blocking_or_accumulating_workers()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var picker = new DesktopPickerService(new TestAppLocalizer(), _ =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            try { release.Wait(TimeSpan.FromSeconds(10)); return true; }
            finally { returned.TrySetResult(); }
        });
        try
        {
            var watch = Stopwatch.StartNew();
            var first = picker.ResolveInitialDirectoryAsync(@"\\offline\share", default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            for (var i = 0; i < 20; i++)
                Assert.Null(await picker.ResolveInitialDirectoryAsync(@"\\offline\share", default));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task Cancelled_probe_keeps_its_worker_lease_until_actual_io_returns()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var picker = new DesktopPickerService(new TestAppLocalizer(), _ =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            try { release.Wait(TimeSpan.FromSeconds(10)); return true; }
            finally { returned.TrySetResult(); }
        });
        try
        {
            var first = picker.ResolveInitialDirectoryAsync(@"Z:\offline", cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Null(await picker.ResolveInitialDirectoryAsync(@"Z:\offline", default));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task Fast_valid_path_is_preserved_and_missing_path_is_ignored()
    {
        var picker = new DesktopPickerService(new TestAppLocalizer(), path => path == @"C:\Root");
        Assert.Equal(@"C:\Root", await picker.ResolveInitialDirectoryAsync(@"C:\Root", default));
        Assert.Null(await picker.ResolveInitialDirectoryAsync(@"C:\missing", default));
        Assert.Null(await picker.ResolveInitialDirectoryAsync(null, default));
    }
}
