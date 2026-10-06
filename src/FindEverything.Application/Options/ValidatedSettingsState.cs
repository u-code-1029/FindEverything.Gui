using Microsoft.Extensions.Options;

namespace FindEverything.Application.Options;

public interface IValidatedSettingsState<out T>
    where T : class
{
    T Current { get; }
}

public interface IValidatedSettingsUpdater<in T>
    where T : class
{
    void Publish(T value);
}

internal sealed class ValidatedSettingsState<T> :
    IValidatedSettingsState<T>,
    IValidatedSettingsUpdater<T>,
    IDisposable
    where T : class
{
    private readonly IDisposable? _subscription;
    private T _current;

    public ValidatedSettingsState(IOptionsMonitor<T> monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _current = monitor.CurrentValue;
        _subscription = monitor.OnChange(Publish);
    }

    public T Current => Volatile.Read(ref _current);

    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref _current, value);
    }

    public void Dispose() => _subscription?.Dispose();
}

public interface IUserSettingsWriter
{
    Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default);
}

public sealed record UserSettingsUpdate(
    WorkspaceOptions Workspace,
    IndexingOptions Indexing,
    AppearanceOptions Appearance);
