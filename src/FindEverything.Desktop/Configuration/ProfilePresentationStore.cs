using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public enum ColumnWidthPreset { Default, Narrow, Wide }
public sealed record ProfilePresentationChangedEventArgs(string ProfileId, IReadOnlyList<string> ChangedFields);

/// <summary>GUI preferences only: never part of the parser descriptor or projection fingerprint.</summary>
public interface IProfilePresentationStore
{
    event EventHandler<ProfilePresentationChangedEventArgs>? Changed;
    Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadAsync(string profileId, CancellationToken cancellationToken = default);
    Task SaveAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> fields, CancellationToken cancellationToken = default);
}

public sealed class ProfilePresentationStore(AppPaths paths, IGridLayoutStore layoutStore,
    ILogger<ProfilePresentationStore> logger) : IProfilePresentationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter<ColumnWidthPreset>() },
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ColumnWidthPreset>> _cache = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler<ProfilePresentationChangedEventArgs>? Changed;

    public async Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var file = GetPath(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(profileId, file, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadCoreAsync(string profileId, string file, CancellationToken ct)
    {
        if (_cache.TryGetValue(profileId, out var cached)) return cached;
        var fields = new Dictionary<string, ColumnWidthPreset>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(file))
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                foreach (var (id, preset) in JsonSerializer.Deserialize<Dictionary<string, ColumnWidthPreset>>(json, JsonOptions) ?? [])
                    if (!string.IsNullOrWhiteSpace(id) && Enum.IsDefined(preset)) fields[id] = preset;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { logger.LogWarning(ex, "Could not read profile presentation {File}.", file); }
        return _cache[profileId] = fields;
    }

    public async Task SaveAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> fields, CancellationToken cancellationToken = default)
    {
        var file = GetPath(profileId);
        ArgumentNullException.ThrowIfNull(fields);
        var snapshot = fields.ToDictionary(static x => x.Key,
            static x => Enum.IsDefined(x.Value) ? x.Value : ColumnWidthPreset.Default, StringComparer.OrdinalIgnoreCase);
        string[] changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var old = await LoadCoreAsync(profileId, file, cancellationToken).ConfigureAwait(false);
            changed = old.Keys.Concat(snapshot.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => old.GetValueOrDefault(id) != snapshot.GetValueOrDefault(id)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporaryFile = file + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryFile, file, overwrite: true);
            }
            finally { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
            _cache[profileId] = snapshot;
            // A changed preset supersedes only that field's manual resize.
            if (changed.Length > 0)
            {
                var layout = layoutStore.Get(profileId).Values.Select(column => changed.Contains(column.FieldId, StringComparer.OrdinalIgnoreCase)
                    ? column with { WidthMode = GridColumnWidthMode.Auto } : column).ToArray();
                layoutStore.Save(profileId, layout);
            }
        }
        finally { _gate.Release(); }
        if (changed.Length > 0) Changed?.Invoke(this, new(profileId, changed));
    }

    private string GetPath(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId is "." or ".." || profileId.IndexOfAny(['/', '\\', ':']) >= 0 || profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid profile identifier.", nameof(profileId));
        return Path.Combine(paths.UserProfilesDirectory, profileId, "presentation.json");
    }
}
