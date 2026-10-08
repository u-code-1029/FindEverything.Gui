using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

public enum ApplicationOperationKind
{
    Exclusive = 0,
    IndexLoad = 1,
    IndexWrite = 2,
    ProfileDiscovery = 3,
    ProfileWrite = 4,
}

public sealed class ApplicationOperationStateChangedEventArgs(
    ApplicationOperationKind? previousKind,
    ApplicationOperationKind? currentKind) : EventArgs
{
    public ApplicationOperationKind? PreviousKind { get; } = previousKind;

    public ApplicationOperationKind? CurrentKind { get; } = currentKind;
}

public sealed class ApplicationOperationBusyException(ApplicationOperationKind? activeKind)
    : InvalidOperationException("이미 다른 작업이 실행 중입니다.")
{
    public ApplicationOperationKind? ActiveKind { get; } = activeKind;
}

public interface IApplicationOperationCoordinator
{
    event EventHandler<ApplicationOperationStateChangedEventArgs>? StateChanged;

    bool IsRunning { get; }

    ApplicationOperationKind? CurrentKind { get; }

    Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task RunAsync(
        ApplicationOperationKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        ApplicationOperationKind replaceableKind,
        ApplicationOperationKind replacementKind,
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
    private ApplicationOperationKind? _currentKind;

    public event EventHandler<ApplicationOperationStateChangedEventArgs>? StateChanged;

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

    public ApplicationOperationKind? CurrentKind
    {
        get
        {
            lock (_gate)
            {
                return _activeTask is { IsCompleted: false } ? _currentKind : null;
            }
        }
    }

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        RunAsync(ApplicationOperationKind.Exclusive, operation, cancellationToken);

    public Task RunAsync(
        ApplicationOperationKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Task startedTask;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                throw new ApplicationOperationBusyException(_currentKind);
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = source;
            _currentKind = kind;
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
            startedTask = _activeTask;
        }

        RaiseStateChanged(previousKind: null, currentKind: kind);
        return startedTask;
    }

    public async Task ReplaceAsync(
        ApplicationOperationKind replaceableKind,
        ApplicationOperationKind replacementKind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(replaceableKind))
        {
            throw new ArgumentOutOfRangeException(nameof(replaceableKind));
        }

        if (!Enum.IsDefined(replacementKind))
        {
            throw new ArgumentOutOfRangeException(nameof(replacementKind));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? replacedTask;
            Task? replacementTask;
            lock (_gate)
            {
                if (_activeTask is not { IsCompleted: false })
                {
                    var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _activeCancellation = source;
                    _currentKind = replacementKind;
                    _activeTask = Task.Factory
                        .StartNew(
                            () => RunCoreAsync(operation, source),
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default)
                        .Unwrap();
                    replacementTask = _activeTask;
                    replacedTask = null;
                }
                else
                {
                    if (_currentKind != replaceableKind)
                    {
                        throw new ApplicationOperationBusyException(_currentKind);
                    }

                    try
                    {
                        _activeCancellation?.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Completion won the race. The next loop claims the slot.
                    }

                    replacedTask = _activeTask;
                    replacementTask = null;
                }
            }

            if (replacementTask is not null)
            {
                RaiseStateChanged(previousKind: null, currentKind: replacementKind);
                await replacementTask.ConfigureAwait(false);
                return;
            }

            try
            {
                await replacedTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The replaceable operation observed the cancellation as intended.
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    exception,
                    "The replaceable {OperationKind} operation ended while being replaced.",
                    replaceableKind);
            }
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Shutdown is a join point, not a second observer for the operation
            // result. The initiating view model still receives the original
            // failure; shutdown can safely continue to dispose and restart.
            logger.LogWarning(
                exception,
                "The active operation ended with an error during shutdown.");
        }
    }

    private async Task RunCoreAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource source)
    {
        var stateChanged = false;
        ApplicationOperationKind? completedKind = null;
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
                    completedKind = _currentKind;
                    _activeCancellation = null;
                    _activeTask = null;
                    _currentKind = null;
                    stateChanged = true;
                }
            }

            source.Dispose();
            if (stateChanged)
            {
                RaiseStateChanged(completedKind, currentKind: null);
            }
        }
    }

    private void RaiseStateChanged(
        ApplicationOperationKind? previousKind,
        ApplicationOperationKind? currentKind)
    {
        var handlers = StateChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new ApplicationOperationStateChangedEventArgs(previousKind, currentKind);
        foreach (var handler in handlers.Cast<EventHandler<ApplicationOperationStateChangedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "An operation-state listener failed.");
            }
        }
    }
}
