using FindEverything.Application.Options;
using System.Collections.ObjectModel;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

public sealed record WorkspaceSnapshot(
    string? SelectedProfileId,
    string? RootPath,
    string? DatabasePath,
    IReadOnlyDictionary<string, string>? ProfileDatabasePaths = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ProfileEntryPaths = null);

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

    Task SetProfileDatabasePathAsync(
        string profileId,
        string? databasePath,
        CancellationToken cancellationToken = default);

    Task SetProfileEntryPathsAsync(string profileId, IReadOnlyList<string>? entryPaths,
        CancellationToken cancellationToken = default) => UpdateAsync(current => current with
        { ProfileEntryPaths = ProfileEntryPathSnapshots.Replace(current.ProfileEntryPaths, profileId, entryPaths) }, cancellationToken);
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
        IUserSettingsWriter settingsWriter)
    {
        ArgumentNullException.ThrowIfNull(workspaceSettings);

        _settingsWriter = settingsWriter;

        var workspace = workspaceSettings.Current;
        _current = new WorkspaceSnapshot(
            workspace.SelectedProfileId,
            NormalizeOptionalPath(workspace.RootPath),
            // DatabasePath is the migration fallback used by previous releases.
            // It now becomes the file-search override only; profile indexes are
            // isolated and resolved through ProfileDatabasePaths.
            NormalizeOptionalPath(
                FirstNonBlank(workspace.FileSearchDatabasePath, workspace.DatabasePath)),
            NormalizeProfileDatabasePaths(workspace.ProfileDatabasePaths),
            NormalizeProfileEntryPaths(workspace.ProfileEntryPaths?.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase)));
    }

    public WorkspaceSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler<WorkspaceChangedEventArgs>? Changed;

    public async Task SaveAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The fourth positional value was added after the original snapshot
            // contract. A null value therefore means "not supplied" for older
            // callers and must not erase per-profile overrides.
            var current = Volatile.Read(ref _current);
            var normalized = NormalizeSnapshot(snapshot with
            {
                ProfileDatabasePaths = snapshot.ProfileDatabasePaths
                    ?? current.ProfileDatabasePaths,
                ProfileEntryPaths = snapshot.ProfileEntryPaths ?? current.ProfileEntryPaths,
            });
            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Workspace: CreatePersistedOptions(normalized)),
                cancellationToken).ConfigureAwait(false);

            var previous = Interlocked.Exchange(ref _current, normalized);
            if (!SnapshotsEqual(previous, normalized))
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
            var normalized = NormalizeSnapshot(requested with
            {
                ProfileDatabasePaths = requested.ProfileDatabasePaths ?? previous.ProfileDatabasePaths,
                ProfileEntryPaths = requested.ProfileEntryPaths ?? previous.ProfileEntryPaths,
            });
            if (SnapshotsEqual(previous, normalized))
            {
                return;
            }

            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Workspace: CreatePersistedOptions(normalized)),
                cancellationToken).ConfigureAwait(false);

            _ = Interlocked.Exchange(ref _current, normalized);
            Changed?.Invoke(this, new WorkspaceChangedEventArgs(previous, normalized));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public Task SetProfileDatabasePathAsync(
        string profileId,
        string? databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var normalizedProfileId = profileId.Trim();
        var normalizedPath = NormalizeOptionalPath(databasePath);
        return UpdateAsync(
            current =>
            {
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (current.ProfileDatabasePaths is not null)
                {
                    foreach (var (existingProfileId, existingPath) in current.ProfileDatabasePaths)
                    {
                        paths[existingProfileId] = existingPath;
                    }
                }

                _ = paths.Remove(normalizedProfileId);
                if (normalizedPath is not null)
                {
                    paths[normalizedProfileId] = normalizedPath;
                }

                return current with
                {
                    ProfileDatabasePaths = ToReadOnlyDictionary(paths),
                };
            },
            cancellationToken);
    }

    public void Dispose() => _saveGate.Dispose();

    public Task SetProfileEntryPathsAsync(string profileId, IReadOnlyList<string>? entryPaths,
        CancellationToken cancellationToken = default) => UpdateAsync(current => current with
        { ProfileEntryPaths = ProfileEntryPathSnapshots.Replace(current.ProfileEntryPaths, profileId, entryPaths) }, cancellationToken);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));

    private static WorkspaceSnapshot NormalizeSnapshot(WorkspaceSnapshot snapshot) =>
        new(
            NormalizeOptional(snapshot.SelectedProfileId),
            NormalizeOptionalPath(snapshot.RootPath),
            NormalizeOptionalPath(snapshot.DatabasePath),
            NormalizeProfileDatabasePaths(snapshot.ProfileDatabasePaths),
            NormalizeProfileEntryPaths(snapshot.ProfileEntryPaths));

    private static WorkspaceOptions CreatePersistedOptions(WorkspaceSnapshot snapshot) =>
        new()
        {
            SelectedProfileId = snapshot.SelectedProfileId,
            RootPath = snapshot.RootPath,
            // Clear the legacy shared value when the workspace is next saved.
            DatabasePath = null,
            FileSearchDatabasePath = snapshot.DatabasePath,
            ProfileDatabasePaths = snapshot.ProfileDatabasePaths is null
                ? null
                : new Dictionary<string, string>(
                    snapshot.ProfileDatabasePaths,
                    StringComparer.OrdinalIgnoreCase),
            ProfileEntryPaths = snapshot.ProfileEntryPaths?.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        };

    private static IReadOnlyDictionary<string, string> NormalizeProfileDatabasePaths(
        IReadOnlyDictionary<string, string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return EmptyProfileDatabasePaths.Instance;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, databasePath) in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            normalized.Add(profileId.Trim(), Path.GetFullPath(databasePath));
        }

        return ToReadOnlyDictionary(normalized);
    }

    private static IReadOnlyDictionary<string, string> ToReadOnlyDictionary(
        Dictionary<string, string> paths) =>
        paths.Count == 0
            ? EmptyProfileDatabasePaths.Instance
            : new ReadOnlyDictionary<string, string>(paths);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeProfileEntryPaths(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? paths)
    {
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (paths is not null)
        {
            if (paths.Count > 256) throw new ArgumentException("At most 256 profile location sets are supported.", nameof(paths));
            foreach (var (profileId, entries) in paths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
                if (profileId.Trim().Length > 128) throw new ArgumentException("Profile identifiers must not exceed 128 characters.", nameof(paths));
                normalized.Add(profileId.Trim(), ProfileEntryPathSnapshots.Normalize(entries));
            }
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(normalized);
    }

    private static bool SnapshotsEqual(WorkspaceSnapshot left, WorkspaceSnapshot right) =>
        string.Equals(left.SelectedProfileId, right.SelectedProfileId, StringComparison.Ordinal)
        && PathsEqual(left.RootPath, right.RootPath)
        && PathsEqual(left.DatabasePath, right.DatabasePath)
        && DictionariesEqual(left.ProfileDatabasePaths, right.ProfileDatabasePaths)
        && ProfileEntryPathSnapshots.Equal(left.ProfileEntryPaths, right.ProfileEntryPaths);

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if ((left?.Count ?? 0) != (right?.Count ?? 0))
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        foreach (var (profileId, path) in left)
        {
            var matchingPath = right!
                .Where(pair => string.Equals(
                    pair.Key,
                    profileId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(static pair => pair.Value)
                .FirstOrDefault();
            if (matchingPath is null || !PathsEqual(path, matchingPath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private sealed class EmptyProfileDatabasePaths
    {
        public static IReadOnlyDictionary<string, string> Instance { get; } =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Copies profile-specific lists so callers cannot mutate a published workspace snapshot.</summary>
public static class ProfileEntryPathSnapshots
{
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ProfileWorkspaceManifestValidator.MaximumEntryPaths)
            throw new ArgumentException("At most 64 starting locations are supported for one profile.", nameof(entries));
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var value = entry?.Trim();
            if (!ProfileWorkspaceManifestValidator.IsAbsoluteEntryPath(value))
                throw new ArgumentException("Starting locations must be fully qualified drive or UNC paths.", nameof(entries));
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value!));
            if (seen.Add(path)) values.Add(path);
        }
        return Array.AsReadOnly(values.ToArray());
    }

    public static IReadOnlyList<string>? Get(IReadOnlyDictionary<string, IReadOnlyList<string>>? values, string profileId) =>
        values?.FirstOrDefault(pair => pair.Key.Equals(profileId, StringComparison.OrdinalIgnoreCase)).Value;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Replace(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? values, string profileId, IReadOnlyList<string>? paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var copy = values?.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray()), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        copy.Remove(profileId.Trim());
        if (paths is not null) copy[profileId.Trim()] = Array.AsReadOnly(paths.ToArray());
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }

    public static bool Equal(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    public static bool Equal(IReadOnlyDictionary<string, IReadOnlyList<string>>? left,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? right) => (left?.Count ?? 0) == (right?.Count ?? 0)
        && (left is null || left.All(pair => Equal(pair.Value, Get(right, pair.Key))));
}
