using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

public interface IApplicationOperationCoordinator
{
    bool IsRunning { get; }

    Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    void Cancel();

    Task CancelAndWaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationOperationCoordinator(
    ILogger<ApplicationOperationCoordinator> logger) : IApplicationOperationCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeTask;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _activeTask is { IsCompleted: false };
            }
        }
    }

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("이미 다른 작업이 실행 중입니다.");
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = source;
            // Do not invoke user code while holding _gate. An async delegate can
            // throw or complete before its first await; running it inline here
            // would make RunCoreAsync's finally block re-enter the same lock.
            _activeTask = Task.Factory
                .StartNew(
                    () => RunCoreAsync(operation, source),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
            return _activeTask;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            try
            {
                _activeCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race with cancellation.
            }
        }
    }

    public async Task CancelAndWaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task? activeTask;
        lock (_gate)
        {
            try
            {
                _activeCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race with shutdown.
            }

            activeTask = _activeTask;
        }

        if (activeTask is null)
        {
            return;
        }

        try
        {
            await activeTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancellation of the operation itself is the expected shutdown path.
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "The active index operation did not stop within {ShutdownTimeout}.",
                timeout);
        }
    }

    private async Task RunCoreAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource source)
    {
        try
        {
            await operation(source.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, source))
                {
                    _activeCancellation = null;
                    _activeTask = null;
                }
            }

            source.Dispose();
        }
    }
}
