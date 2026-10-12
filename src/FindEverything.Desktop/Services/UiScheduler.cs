namespace FindEverything.Desktop.Services;

/// <summary>Dispatches UI updates without making view models depend on Application.Current.</summary>
public interface IUiScheduler
{
    void Post(Action action);

    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public sealed class ImmediateUiScheduler : IUiScheduler
{
    public static ImmediateUiScheduler Instance { get; } = new();

    public void Post(Action action) => action();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

public sealed class WpfUiScheduler : IUiScheduler
{
    public void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            try { _ = dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
        }
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        }

        try { return dispatcher.InvokeAsync(action).Task.Unwrap(); }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        }
    }
}
