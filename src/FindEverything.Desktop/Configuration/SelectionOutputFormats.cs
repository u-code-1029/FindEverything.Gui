using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public sealed record SelectionOutputFormatDefinition(
    string Id,
    string DisplayName,
    string Template,
    string ItemSeparator,
    string? ProfileId)
{
    public bool IsBuiltIn => string.Equals(
        Id,
        SelectionOutputFormatDefaults.FullPathLinesId,
        StringComparison.OrdinalIgnoreCase);
}

public static class SelectionOutputFormatDefaults
{
    public const string FullPathLinesId = "builtin.full-path-lines";

    public static SelectionOutputFormatDefinition FullPathLines { get; } = new(
        FullPathLinesId,
        "전체 경로 (한 줄에 하나)",
        "{FullPath}",
        Environment.NewLine,
        ProfileId: null);
}

public interface ISelectionOutputFormatStore
{
    event EventHandler? Changed;

    IReadOnlyList<SelectionOutputFormatDefinition> GetAll();

    IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId);

    void Save(SelectionOutputFormatDefinition definition);

    bool Delete(string id);
}

public sealed class SelectionOutputFormatStore : ISelectionOutputFormatStore
{
    private const int CurrentFileVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly AppPaths _paths;
    private readonly ILogger<SelectionOutputFormatStore> _logger;
    private Dictionary<string, SelectionOutputFormatDefinition>? _customFormats;

    public SelectionOutputFormatStore(
        AppPaths paths,
        ILogger<SelectionOutputFormatStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SelectionOutputFormatDefinition> GetAll()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return CreateSnapshot(static _ => true);
        }
    }

    public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return CreateSnapshot(format =>
                string.IsNullOrWhiteSpace(format.ProfileId)
                || (!string.IsNullOrWhiteSpace(profileId)
                    && string.Equals(
                        format.ProfileId,
                        profileId,
                        StringComparison.OrdinalIgnoreCase)));
        }
    }

    public void Save(SelectionOutputFormatDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = NormalizeAndValidate(definition);
        if (normalized.IsBuiltIn)
        {
            throw new InvalidOperationException("기본 출력 포맷은 수정할 수 없습니다.");
        }

        lock (_sync)
        {
            EnsureLoaded();
            var hadPrevious = _customFormats!.TryGetValue(normalized.Id, out var previous);
            _customFormats[normalized.Id] = normalized;
            try
            {
                WriteAtomically();
            }
            catch
            {
                if (hadPrevious)
                {
                    _customFormats[normalized.Id] = previous!;
                }
                else
                {
                    _customFormats.Remove(normalized.Id);
                }

                throw;
            }
        }

        RaiseChanged();
    }

    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.Equals(
                id.Trim(),
                SelectionOutputFormatDefaults.FullPathLinesId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var removed = false;
        lock (_sync)
        {
            EnsureLoaded();
            if (!_customFormats!.Remove(id.Trim(), out var previous))
            {
                return false;
            }

            try
            {
                WriteAtomically();
                removed = true;
            }
            catch
            {
                _customFormats[previous.Id] = previous;
                throw;
            }
        }

        if (removed)
        {
            RaiseChanged();
        }

        return removed;
    }

    private IReadOnlyList<SelectionOutputFormatDefinition> CreateSnapshot(
        Func<SelectionOutputFormatDefinition, bool> predicate)
    {
        var custom = _customFormats!.Values
            .Where(predicate)
            .OrderBy(static format => format.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        return new[] { SelectionOutputFormatDefaults.FullPathLines }
            .Concat(custom)
            .ToArray();
    }

    private void EnsureLoaded()
    {
        if (_customFormats is not null)
        {
            return;
        }

        _customFormats = new Dictionary<string, SelectionOutputFormatDefinition>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_paths.SelectionOutputFormatsFile))
            {
                return;
            }

            using var stream = new FileStream(
                _paths.SelectionOutputFormatsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<SelectionOutputFormatDocument>(
                stream,
                SerializerOptions);
            if (document is null || document.Version != CurrentFileVersion)
            {
                _logger.LogWarning(
                    "Unsupported selection output format file version in {FormatFile}.",
                    _paths.SelectionOutputFormatsFile);
                return;
            }

            foreach (var definition in document.Formats ?? [])
            {
                try
                {
                    var normalized = NormalizeAndValidate(definition);
                    if (!normalized.IsBuiltIn)
                    {
                        _customFormats[normalized.Id] = normalized;
                    }
                }
                catch (ArgumentException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Ignored an invalid selection output format in {FormatFile}.",
                        _paths.SelectionOutputFormatsFile);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            _logger.LogWarning(
                exception,
                "Could not read selection output formats from {FormatFile}.",
                _paths.SelectionOutputFormatsFile);
        }
    }

    private void WriteAtomically()
    {
        Directory.CreateDirectory(_paths.LocalDataDirectory);
        var temporaryPath = _paths.SelectionOutputFormatsFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new SelectionOutputFormatDocument(
                CurrentFileVersion,
                _customFormats!.Values
                    .OrderBy(static format => format.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _paths.SelectionOutputFormatsFile, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            _logger.LogWarning(
                exception,
                "Could not save selection output formats to {FormatFile}.",
                _paths.SelectionOutputFormatsFile);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static SelectionOutputFormatDefinition NormalizeAndValidate(
        SelectionOutputFormatDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var id = definition.Id?.Trim() ?? string.Empty;
        var displayName = definition.DisplayName?.Trim() ?? string.Empty;
        var template = definition.Template ?? string.Empty;
        var itemSeparator = definition.ItemSeparator ?? string.Empty;
        var profileId = string.IsNullOrWhiteSpace(definition.ProfileId)
            ? null
            : definition.ProfileId.Trim();

        if (id.Length is < 1 or > 128)
        {
            throw new ArgumentException("출력 포맷 ID는 1~128자여야 합니다.", nameof(definition));
        }

        if (displayName.Length is < 1 or > 100)
        {
            throw new ArgumentException("출력 포맷 이름은 1~100자여야 합니다.", nameof(definition));
        }

        if (template.Length is < 1 or > 16_384)
        {
            throw new ArgumentException("출력 템플릿은 1~16,384자여야 합니다.", nameof(definition));
        }

        if (itemSeparator.Length > 1_024)
        {
            throw new ArgumentException("항목 구분자는 1,024자 이하여야 합니다.", nameof(definition));
        }

        if (profileId is { Length: > 128 })
        {
            throw new ArgumentException("프로필 ID는 128자 이하여야 합니다.", nameof(definition));
        }

        return new SelectionOutputFormatDefinition(
            id,
            displayName,
            template,
            itemSeparator,
            profileId);
    }

    private sealed record SelectionOutputFormatDocument(
        int Version,
        IReadOnlyList<SelectionOutputFormatDefinition>? Formats);

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A selection output format subscriber failed.");
            }
        }
    }
}
