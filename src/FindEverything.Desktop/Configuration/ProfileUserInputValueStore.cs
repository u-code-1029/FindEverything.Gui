using System.Text.Json;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public interface IProfileUserInputValueStore
{
    IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath);

    Task SetValueAsync(
        string profileId,
        string absolutePath,
        string fieldId,
        string value,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileUserInputValueStore : IProfileUserInputValueStore
{
    private const long MaximumFileLength = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<ProfileUserInputValueStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<UserInputItemKey, Dictionary<string, string>> _values =
        new(UserInputItemKeyComparer.Instance);
    private bool _loaded;

    public ProfileUserInputValueStore(
        AppPaths paths,
        ILogger<ProfileUserInputValueStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, string> GetValues(
        string profileId,
        string absolutePath)
    {
        var normalizedProfileId = RequireToken(profileId, nameof(profileId));
        var normalizedPath = NormalizeAbsolutePath(absolutePath);
        _gate.Wait();
        try
        {
            EnsureLoaded();
            return _values.TryGetValue(
                    new UserInputItemKey(normalizedProfileId, normalizedPath),
                    out var values)
                ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetValueAsync(
        string profileId,
        string absolutePath,
        string fieldId,
        string value,
        CancellationToken cancellationToken = default)
    {
        var key = new UserInputItemKey(
            RequireToken(profileId, nameof(profileId)),
            NormalizeAbsolutePath(absolutePath));
        var normalizedFieldId = RequireToken(fieldId, nameof(fieldId));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > ProfileManifestLimits.MaximumUserInputValueLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"사용자 입력 값은 최대 {ProfileManifestLimits.MaximumUserInputValueLength:N0}자까지 저장할 수 있습니다.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (!_values.TryGetValue(key, out var itemValues))
            {
                itemValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _values[key] = itemValues;
            }

            itemValues[normalizedFieldId] = value;
            await WriteAtomicallyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var file = new FileInfo(_paths.ProfileUserInputValuesFile);
            if (!file.Exists || file.Length > MaximumFileLength)
            {
                return;
            }

            var document = JsonSerializer.Deserialize<UserInputDocument>(
                File.ReadAllText(file.FullName),
                SerializerOptions);
            foreach (var entry in document?.Entries ?? [])
            {
                if (entry.ProfileId is null
                    || entry.AbsolutePath is null
                    || entry.FieldId is null
                    || entry.Value is null
                    || entry.Value.Length > ProfileManifestLimits.MaximumUserInputValueLength)
                {
                    continue;
                }

                try
                {
                    var key = new UserInputItemKey(
                        RequireToken(entry.ProfileId, nameof(entry.ProfileId)),
                        NormalizeAbsolutePath(entry.AbsolutePath));
                    var fieldId = RequireToken(entry.FieldId, nameof(entry.FieldId));
                    if (!_values.TryGetValue(key, out var itemValues))
                    {
                        itemValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _values[key] = itemValues;
                    }

                    itemValues[fieldId] = entry.Value;
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _logger.LogWarning(exception, "Ignored an invalid persisted user-input entry.");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Could not load persisted profile user-input values.");
        }
    }

    private async Task WriteAtomicallyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.LocalDataDirectory);
        var document = new UserInputDocument
        {
            Version = 1,
            Entries = _values
                .SelectMany(static pair => pair.Value.Select(value => new UserInputEntry
                {
                    ProfileId = pair.Key.ProfileId,
                    AbsolutePath = pair.Key.AbsolutePath,
                    FieldId = value.Key,
                    Value = value.Value,
                }))
                .OrderBy(static entry => entry.ProfileId!, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.AbsolutePath!, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.FieldId!, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        var temporaryPath = Path.Combine(
            _paths.LocalDataDirectory,
            $".{Path.GetFileName(_paths.ProfileUserInputValuesFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _paths.ProfileUserInputValuesFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string RequireToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string NormalizeAbsolutePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var fullPath = Path.GetFullPath(absolutePath);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("절대 경로가 필요합니다.", nameof(absolutePath));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private sealed class UserInputDocument
    {
        public int Version { get; set; }

        public List<UserInputEntry>? Entries { get; set; }
    }

    private sealed class UserInputEntry
    {
        public string? ProfileId { get; set; }

        public string? AbsolutePath { get; set; }

        public string? FieldId { get; set; }

        public string? Value { get; set; }
    }

    private readonly record struct UserInputItemKey(
        string ProfileId,
        string AbsolutePath);

    private sealed class UserInputItemKeyComparer : IEqualityComparer<UserInputItemKey>
    {
        public static UserInputItemKeyComparer Instance { get; } = new();

        public bool Equals(UserInputItemKey x, UserInputItemKey y) =>
            string.Equals(x.ProfileId, y.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.AbsolutePath, y.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(UserInputItemKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.ProfileId, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.AbsolutePath, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
