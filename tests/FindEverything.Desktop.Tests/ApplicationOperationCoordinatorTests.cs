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

    private static ApplicationOperationCoordinator CreateCoordinator() =>
        new(NullLogger<ApplicationOperationCoordinator>.Instance);
}
