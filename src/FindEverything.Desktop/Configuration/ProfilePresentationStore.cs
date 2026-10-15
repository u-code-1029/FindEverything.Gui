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
    async Task<bool> TryReplaceAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> expectedFields,
        IReadOnlyDictionary<string, ColumnWidthPreset> replacementFields, CancellationToken cancellationToken = default,
        bool resetManualWidths = false)
    {
        var current = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (current.Count != expectedFields.Count || current.Any(pair =>
                !expectedFields.TryGetValue(pair.Key, out var expected) || expected != pair.Value)) return false;
        await SaveAsync(profileId, replacementFields, cancellationToken).ConfigureAwait(false);
        return true;
    }
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

    private async Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadCoreAsync(string profileId, string file, CancellationToken ct,
        bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(profileId, out var cached)) return cached;
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
        catch (Exception ex) when (!fresh && (ex is IOException or UnauthorizedAccessException or JsonException))
        { logger.LogWarning(ex, "Could not read profile presentation {File}.", file); }
        return _cache[profileId] = fields;
    }

    public Task SaveAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> fields, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(profileId, null, fields, resetManualWidths: true, cancellationToken);

    public Task<bool> TryReplaceAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> expectedFields,
        IReadOnlyDictionary<string, ColumnWidthPreset> replacementFields, CancellationToken cancellationToken = default,
        bool resetManualWidths = false)
    {
        ArgumentNullException.ThrowIfNull(expectedFields);
        return SaveCoreAsync(profileId, expectedFields, replacementFields, resetManualWidths, cancellationToken);
    }

    private async Task<bool> SaveCoreAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset>? expected,
        IReadOnlyDictionary<string, ColumnWidthPreset> fields, bool resetManualWidths, CancellationToken cancellationToken)
    {
        var file = GetPath(profileId);
        ArgumentNullException.ThrowIfNull(fields);
        var snapshot = fields.ToDictionary(static x => x.Key,
            static x => Enum.IsDefined(x.Value) ? x.Value : ColumnWidthPreset.Default, StringComparer.OrdinalIgnoreCase);
        string[] changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousBytes = File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
            var old = await LoadCoreAsync(profileId, file, cancellationToken, fresh: true).ConfigureAwait(false);
            if (expected is not null && (old.Count != expected.Count
                    || old.Any(pair => !expected.TryGetValue(pair.Key, out var value) || pair.Value != value))) return false;
            changed = old.Keys.Concat(snapshot.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => old.GetValueOrDefault(id) != snapshot.GetValueOrDefault(id)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporaryFile = file + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var currentBytes = File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
                if (previousBytes is null ? currentBytes is not null : currentBytes is null || !previousBytes.AsSpan().SequenceEqual(currentBytes))
                    throw new IOException("Profile presentation changed while it was being saved. Please retry.");
                File.Move(temporaryFile, file, overwrite: true);
            }
            finally { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
            _cache[profileId] = snapshot;
            // A changed preset supersedes only that field's manual resize.
            if (resetManualWidths && changed.Length > 0)
            {
                var layout = layoutStore.Get(profileId).Values.Select(column => changed.Contains(column.FieldId, StringComparer.OrdinalIgnoreCase)
                    ? column with { WidthMode = GridColumnWidthMode.Auto } : column).ToArray();
                layoutStore.Save(profileId, layout);
            }
        }
        finally { _gate.Release(); }
        if (changed.Length > 0)
        {
            var handlers = Changed;
            if (handlers is not null)
                foreach (EventHandler<ProfilePresentationChangedEventArgs> handler in handlers.GetInvocationList())
                    try { handler(this, new(profileId, changed)); }
                    catch (Exception exception) { logger.LogError(exception, "A profile presentation subscriber failed."); }
        }
        return true;
    }

    private string GetPath(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId is "." or ".." || profileId.IndexOfAny(['/', '\\', ':']) >= 0 || profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid profile identifier.", nameof(profileId));
        return Path.Combine(paths.UserProfilesDirectory, profileId, "presentation.json");
    }
}
