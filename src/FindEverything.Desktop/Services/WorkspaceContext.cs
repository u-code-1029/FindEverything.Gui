using FindEverything.Application.Options;
using FindEverything.Desktop.Configuration;

namespace FindEverything.Desktop.Services;

public sealed record WorkspaceSnapshot(
    string? SelectedProfileId,
    string? RootPath,
    string DatabasePath);

public sealed class WorkspaceChangedEventArgs(
    WorkspaceSnapshot previous,
    WorkspaceSnapshot current) : EventArgs
{
    public WorkspaceSnapshot Previous { get; } = previous;

    public WorkspaceSnapshot Current { get; } = current;
}

public interface IWorkspaceContext
{
    WorkspaceSnapshot Current { get; }

    event EventHandler<WorkspaceChangedEventArgs>? Changed;

    Task SaveAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Func<WorkspaceSnapshot, WorkspaceSnapshot> update,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the single workspace selection shared by file search and structured views.
/// Persisting through this service keeps both singleton view models in sync.
/// </summary>
public sealed class WorkspaceContext : IWorkspaceContext, IDisposable
{
    private readonly IUserSettingsWriter _settingsWriter;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private WorkspaceSnapshot _current;

    public WorkspaceContext(
        IValidatedSettingsState<WorkspaceOptions> workspaceSettings,
        IUserSettingsWriter settingsWriter,
        AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(workspaceSettings);
        ArgumentNullException.ThrowIfNull(paths);

        _settingsWriter = settingsWriter;

        var workspace = workspaceSettings.Current;
        _current = new WorkspaceSnapshot(
            workspace.SelectedProfileId,
            NormalizeOptionalPath(workspace.RootPath),
            string.IsNullOrWhiteSpace(workspace.DatabasePath)
                ? paths.IndexDatabaseFile
                : Path.GetFullPath(workspace.DatabasePath));
    }

    public WorkspaceSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler<WorkspaceChangedEventArgs>? Changed;

    public async Task SaveAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.DatabasePath);

        var normalized = new WorkspaceSnapshot(
            NormalizeOptional(snapshot.SelectedProfileId),
            NormalizeOptionalPath(snapshot.RootPath),
            Path.GetFullPath(snapshot.DatabasePath));

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Workspace: new WorkspaceOptions
                    {
                        SelectedProfileId = normalized.SelectedProfileId,
                        RootPath = normalized.RootPath,
                        DatabasePath = normalized.DatabasePath,
                    }),
                cancellationToken).ConfigureAwait(false);

            var previous = Interlocked.Exchange(ref _current, normalized);
            if (previous != normalized)
            {
                Changed?.Invoke(this, new WorkspaceChangedEventArgs(previous, normalized));
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task UpdateAsync(
        Func<WorkspaceSnapshot, WorkspaceSnapshot> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _current);
            var requested = update(previous)
                ?? throw new InvalidOperationException("The workspace update returned null.");
            ArgumentException.ThrowIfNullOrWhiteSpace(requested.DatabasePath);
            var normalized = new WorkspaceSnapshot(
                NormalizeOptional(requested.SelectedProfileId),
                NormalizeOptionalPath(requested.RootPath),
                Path.GetFullPath(requested.DatabasePath));
            if (previous == normalized)
            {
                return;
            }

            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Workspace: new WorkspaceOptions
                    {
                        SelectedProfileId = normalized.SelectedProfileId,
                        RootPath = normalized.RootPath,
                        DatabasePath = normalized.DatabasePath,
                    }),
                cancellationToken).ConfigureAwait(false);

            _ = Interlocked.Exchange(ref _current, normalized);
            Changed?.Invoke(this, new WorkspaceChangedEventArgs(previous, normalized));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose() => _saveGate.Dispose();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
}
