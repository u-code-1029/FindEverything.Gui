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

/// <summary>
/// A partial settings update. Null sections are preserved from the current
/// user-settings document so independent UI operations cannot overwrite one
/// another with stale snapshots.
/// </summary>
public sealed record UserSettingsUpdate(
    WorkspaceOptions? Workspace = null,
    IndexingOptions? Indexing = null,
    AppearanceOptions? Appearance = null,
    LocalizationOptions? Localization = null);
