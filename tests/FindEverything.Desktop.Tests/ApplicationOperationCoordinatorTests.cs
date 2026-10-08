using FindEverything.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ApplicationOperationCoordinatorTests
{
    [Fact]
    public async Task RunAsync_propagates_a_synchronous_delegate_failure_without_deadlocking()
    {
        var coordinator = CreateCoordinator();

        var operation = coordinator.RunAsync(
            static _ => throw new InvalidOperationException("expected"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("expected", exception.Message);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task CancelAndWaitAsync_cancels_and_joins_the_tracked_operation()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = coordinator.RunAsync(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task RunAsync_exposes_operation_kind_until_the_operation_finishes()
    {
        var coordinator = CreateCoordinator();
        var states = new System.Collections.Concurrent.ConcurrentQueue<
            (ApplicationOperationKind? Previous, ApplicationOperationKind? Current)>();
        coordinator.StateChanged += (_, eventArgs) =>
            states.Enqueue((eventArgs.PreviousKind, eventArgs.CurrentKind));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = coordinator.RunAsync(
            ApplicationOperationKind.IndexLoad,
            async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.IsRunning);
        Assert.Equal(ApplicationOperationKind.IndexLoad, coordinator.CurrentKind);

        await coordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(coordinator.IsRunning);
        Assert.Null(coordinator.CurrentKind);
        Assert.Contains(
            states,
            state => state == (null, ApplicationOperationKind.IndexLoad));
        Assert.Contains(
            states,
            state => state == (ApplicationOperationKind.IndexLoad, null));
    }

    [Fact]
    public async Task ReplaceAsync_cancels_an_index_load_before_claiming_index_write()
    {
        var coordinator = CreateCoordinator();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = coordinator.RunAsync(
            ApplicationOperationKind.IndexLoad,
            async cancellationToken =>
            {
                loadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    loadCancelled.TrySetResult();
                    throw;
                }
            });

        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = coordinator.ReplaceAsync(
            ApplicationOperationKind.IndexLoad,
            ApplicationOperationKind.IndexWrite,
            async cancellationToken =>
            {
                writeStarted.TrySetResult();
                await releaseWrite.Task.WaitAsync(cancellationToken);
            });

        await loadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ApplicationOperationKind.IndexWrite, coordinator.CurrentKind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        releaseWrite.TrySetResult();
        await write;
        Assert.False(coordinator.IsRunning);
        Assert.Null(coordinator.CurrentKind);
    }

    private static ApplicationOperationCoordinator CreateCoordinator() =>
        new(NullLogger<ApplicationOperationCoordinator>.Instance);
}
