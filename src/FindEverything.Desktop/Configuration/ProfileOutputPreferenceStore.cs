using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

/// <summary>Remembers a profile's selected copy format independently of parser configuration.</summary>
public interface IProfileOutputPreferenceStore
{
    string? Get(string profileId);
    Task SaveAsync(string profileId, string? formatId, CancellationToken cancellationToken = default);
    Task<bool> TryReplaceAsync(string profileId, string? expectedFormatId, string? replacementFormatId,
        CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ProfileOutputPreferenceStore : IProfileOutputPreferenceStore
{
    private readonly AppPaths _paths;
    private readonly ILogger<ProfileOutputPreferenceStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly object _queueSync = new();
    private Task _pendingWrite = Task.CompletedTask;
    private IReadOnlyDictionary<string, string> _snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private string FilePath => Path.Combine(_paths.LocalDataDirectory, "profile-output-preferences.json");

    public ProfileOutputPreferenceStore(AppPaths paths, ILogger<ProfileOutputPreferenceStore> logger)
    {
        _paths = paths;
        _logger = logger;
        try { PublishSnapshot(Read()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { _logger.LogWarning(exception, "Could not read profile output preferences from {File}.", FilePath); }
    }

    public string? Get(string profileId)
    {
        ValidateIdentifier(profileId, nameof(profileId));
        return Volatile.Read(ref _snapshot).GetValueOrDefault(profileId);
    }

    public Task SaveAsync(string profileId, string? formatId, CancellationToken cancellationToken = default) =>
        ReplaceAsync(profileId, null, formatId, compare: false, cancellationToken);

    public Task<bool> TryReplaceAsync(string profileId, string? expectedFormatId, string? replacementFormatId,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(profileId, expectedFormatId, replacementFormatId, compare: true, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_queueSync) return _pendingWrite.WaitAsync(cancellationToken);
    }

    private Task<bool> ReplaceAsync(string profileId, string? expected, string? replacement, bool compare,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(profileId, nameof(profileId));
        if (replacement is not null) ValidateIdentifier(replacement, nameof(replacement));
        lock (_queueSync)
        {
            var previousWrite = _pendingWrite;
            var operation = Task.Run(async () =>
            {
                try { await previousWrite.ConfigureAwait(false); }
                catch (Exception) { /* A failed older write must not prevent a later explicit retry. */ }
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // No stale cached snapshot: preserve other profiles and detect later writes before rollback.
                    var previousBytes = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
                    var snapshot = Read();
                    if (compare && !string.Equals(snapshot.GetValueOrDefault(profileId), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        PublishSnapshot(snapshot);
                        return false;
                    }
                    if (replacement is null) snapshot.Remove(profileId);
                    else snapshot[profileId] = replacement;
                    Directory.CreateDirectory(_paths.LocalDataDirectory);
                    var temporaryFile = FilePath + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write,
                                   FileShare.None, 4096, FileOptions.WriteThrough))
                        {
                            JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                            stream.Flush(flushToDisk: true);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentBytes = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
                        if (previousBytes is null ? currentBytes is not null : currentBytes is null || !previousBytes.AsSpan().SequenceEqual(currentBytes))
                            throw new IOException("Profile output preferences changed while they were being saved. Please retry.");
                        File.Move(temporaryFile, FilePath, overwrite: true);
                        PublishSnapshot(snapshot);
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
                        catch (IOException exception) { _logger.LogDebug(exception, "Could not remove temporary output preference file."); }
                    }
                    return true;
                }
            });
            _pendingWrite = operation;
            return operation;
        }
    }

    private void PublishSnapshot(Dictionary<string, string> values) => Volatile.Write(ref _snapshot,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)));

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var source = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOptions)
            ?? throw new InvalidDataException("The profile output preferences document is empty.");
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, formatId) in source)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(formatId)) continue;
            snapshot[profileId] = formatId;
        }
        return snapshot;
    }

    private static void ValidateIdentifier(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 128) throw new ArgumentException("The identifier must not exceed 128 characters.", name);
    }
}
