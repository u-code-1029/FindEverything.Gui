using System.Text.Json;
using FindEverything.Desktop.Localization;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public sealed record SelectionOutputFormatDefinition(
    string Id,
    string DisplayName,
    string Template,
    string ItemSeparator,
    string? ProfileId,
    string Prefix = "",
    string Suffix = "")
{
    public bool IsBuiltIn => string.Equals(
        Id,
        SelectionOutputFormatDefaults.FullPathLinesId,
        StringComparison.OrdinalIgnoreCase);
}

public static class SelectionOutputFormatDefaults
{
    public const string FullPathLinesId = "builtin.full-path-lines";
    public const int MaximumAffixCharacters = 16_384;

    public static SelectionOutputFormatDefinition FullPathLines { get; } =
        CreateFullPathLines();

    public static SelectionOutputFormatDefinition CreateFullPathLines(
        IAppLocalizer? localizer = null) => new(
            FullPathLinesId,
            localizer?.Get(
                "Loc.Output.BuiltIn.FullPathLines",
                "전체 경로 (한 줄에 하나)")
                ?? "전체 경로 (한 줄에 하나)",
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

    Task SaveAsync(SelectionOutputFormatDefinition definition, CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); Save(definition); }, cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return Delete(id); }, cancellationToken);

    Task ReplaceProfileFormatsAsync(string profileId, IReadOnlyList<SelectionOutputFormatDefinition> formats,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var existing in GetAll().Where(format => !format.IsBuiltIn
                     && string.Equals(format.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
            Delete(existing.Id);
        foreach (var format in formats) Save(format);
        return Task.CompletedTask;
    }

    async Task<bool> TryReplaceProfileFormatsAsync(string profileId,
        IReadOnlyList<SelectionOutputFormatDefinition> expectedFormats,
        IReadOnlyList<SelectionOutputFormatDefinition> replacementFormats,
        CancellationToken cancellationToken = default)
    {
        var current = GetAll().Where(format => !format.IsBuiltIn
            && string.Equals(format.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        if (!current.OrderBy(format => format.Id, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                expectedFormats.OrderBy(format => format.Id, StringComparer.OrdinalIgnoreCase))) return false;
        await ReplaceProfileFormatsAsync(profileId, replacementFormats, cancellationToken).ConfigureAwait(false);
        return true;
    }
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
    private readonly IAppLocalizer? _localizer;
    private readonly SelectionOutputFormatDefinition _builtInFormat;
    private Dictionary<string, SelectionOutputFormatDefinition>? _customFormats;
    private IReadOnlyList<SelectionOutputFormatDefinition> _snapshot = [];

    public SelectionOutputFormatStore(
        AppPaths paths,
        ILogger<SelectionOutputFormatStore> logger,
        IAppLocalizer? localizer = null)
    {
        _paths = paths;
        _logger = logger;
        _localizer = localizer;
        _builtInFormat = SelectionOutputFormatDefaults.CreateFullPathLines(localizer);
        EnsureLoaded();
        PublishSnapshot();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => Volatile.Read(ref _snapshot);

    public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId)
    {
        return Array.AsReadOnly(GetAll().Where(format =>
                string.IsNullOrWhiteSpace(format.ProfileId)
                || (!string.IsNullOrWhiteSpace(profileId)
                    && string.Equals(
                        format.ProfileId,
                        profileId,
                        StringComparison.OrdinalIgnoreCase))).ToArray());
    }

    public void Save(SelectionOutputFormatDefinition definition) => SaveAsync(definition).GetAwaiter().GetResult();

    public Task SaveAsync(SelectionOutputFormatDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = NormalizeAndValidate(definition);
        if (normalized.IsBuiltIn)
        {
            throw new InvalidOperationException(L(
                "Loc.Output.Error.BuiltInReadOnly",
                "기본 출력 포맷은 수정할 수 없습니다."));
        }

        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedBytes = ReloadForWrite();
                var previous = _customFormats!;
                _customFormats = new(previous, StringComparer.OrdinalIgnoreCase) { [normalized.Id] = normalized };
                try { WriteAtomically(expectedBytes, compare: true, cancellationToken); }
                catch { _customFormats = previous; throw; }
                PublishSnapshot();
            }
            RaiseChanged();
        }, cancellationToken);
    }

    public bool Delete(string id) => DeleteAsync(id).GetAwaiter().GetResult();

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.Equals(
                id.Trim(),
                SelectionOutputFormatDefaults.FullPathLinesId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedBytes = ReloadForWrite();
                if (!_customFormats!.ContainsKey(id.Trim())) { PublishSnapshot(); return false; }
                var previous = _customFormats;
                _customFormats = new(previous, StringComparer.OrdinalIgnoreCase);
                _customFormats.Remove(id.Trim());
                try { WriteAtomically(expectedBytes, compare: true, cancellationToken); }
                catch { _customFormats = previous; throw; }
                PublishSnapshot();
            }
            RaiseChanged();
            return true;
        }, cancellationToken);
    }

    public Task ReplaceProfileFormatsAsync(string profileId,
        IReadOnlyList<SelectionOutputFormatDefinition> formats, CancellationToken cancellationToken = default) =>
        ReplaceProfileFormatsCoreAsync(profileId, null, formats, cancellationToken);

    public Task<bool> TryReplaceProfileFormatsAsync(string profileId,
        IReadOnlyList<SelectionOutputFormatDefinition> expectedFormats,
        IReadOnlyList<SelectionOutputFormatDefinition> replacementFormats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedFormats);
        return ReplaceProfileFormatsCoreAsync(profileId, expectedFormats, replacementFormats, cancellationToken);
    }

    private Task<bool> ReplaceProfileFormatsCoreAsync(string profileId,
        IReadOnlyList<SelectionOutputFormatDefinition>? expectedFormats,
        IReadOnlyList<SelectionOutputFormatDefinition> formats, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(formats);
        var normalized = formats.Select(NormalizeAndValidate).ToArray();
        if (normalized.Any(format => format.IsBuiltIn
                || !string.Equals(format.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            || normalized.Select(format => format.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("Each custom format must have a unique ID and belong to the target profile.", nameof(formats));

        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read again so a conditional rollback cannot overwrite a later external edit.
                var persistedBytes = ReloadForWrite();
                var current = _customFormats!.Values.Where(format => string.Equals(
                    format.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (expectedFormats is not null && !current.OrderBy(format => format.Id, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(expectedFormats.OrderBy(format => format.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    PublishSnapshot();
                    return false;
                }
                if (normalized.Any(format => _customFormats.TryGetValue(format.Id, out var existing)
                        && !string.Equals(existing.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("An output format ID already belongs to another profile or a shared format.");
                var previous = _customFormats;
                _customFormats = previous.Where(pair => !string.Equals(pair.Value.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var format in normalized) _customFormats.Add(format.Id, format);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteAtomically(persistedBytes, compare: true, cancellationToken);
                }
                catch { _customFormats = previous; throw; }
                PublishSnapshot();
            }

            RaiseChanged();
            return true;
        }, cancellationToken);
    }

    private byte[]? ReloadForWrite()
    {
        var formats = new Dictionary<string, SelectionOutputFormatDefinition>(StringComparer.OrdinalIgnoreCase);
        byte[]? bytes = null;
        if (File.Exists(_paths.SelectionOutputFormatsFile))
        {
            using var stream = new FileStream(_paths.SelectionOutputFormatsFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
            var document = JsonSerializer.Deserialize<SelectionOutputFormatDocument>(bytes, SerializerOptions);
            if (document is null || document.Version != CurrentFileVersion)
                throw new InvalidDataException("The output format document has an unsupported version.");
            foreach (var definition in document.Formats ?? [])
            {
                var normalized = NormalizeAndValidate(definition);
                if (!normalized.IsBuiltIn) formats[normalized.Id] = normalized;
            }
        }
        _customFormats = formats;
        return bytes;
    }

    private void PublishSnapshot()
    {
        var custom = _customFormats!.Values
            .OrderBy(static format => format.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        Volatile.Write(ref _snapshot, Array.AsReadOnly(new[] { _builtInFormat }
            .Concat(custom)
            .ToArray()));
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

    private void WriteAtomically(byte[]? expectedBytes = null, bool compare = false, CancellationToken cancellationToken = default)
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

            cancellationToken.ThrowIfCancellationRequested();
            if (compare)
            {
                var currentBytes = File.Exists(_paths.SelectionOutputFormatsFile) ? File.ReadAllBytes(_paths.SelectionOutputFormatsFile) : null;
                if (expectedBytes is null ? currentBytes is not null : currentBytes is null || !expectedBytes.AsSpan().SequenceEqual(currentBytes))
                    throw new IOException("Output formats changed while they were being saved. Please retry.");
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

    private SelectionOutputFormatDefinition NormalizeAndValidate(
        SelectionOutputFormatDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var id = definition.Id?.Trim() ?? string.Empty;
        var displayName = definition.DisplayName?.Trim() ?? string.Empty;
        var template = definition.Template ?? string.Empty;
        var itemSeparator = definition.ItemSeparator ?? string.Empty;
        var prefix = definition.Prefix ?? string.Empty;
        var suffix = definition.Suffix ?? string.Empty;
        var profileId = string.IsNullOrWhiteSpace(definition.ProfileId)
            ? null
            : definition.ProfileId.Trim();

        if (id.Length is < 1 or > 128)
        {
            throw new ArgumentException(
                L("Loc.Output.Error.IdLength", "출력 포맷 ID는 1~128자여야 합니다."),
                nameof(definition));
        }

        if (displayName.Length is < 1 or > 100)
        {
            throw new ArgumentException(
                L("Loc.Output.Error.NameLength", "출력 포맷 이름은 1~100자여야 합니다."),
                nameof(definition));
        }

        if (template.Length is < 1 or > 16_384)
        {
            throw new ArgumentException(
                L("Loc.Output.Error.TemplateLength", "출력 템플릿은 1~16,384자여야 합니다."),
                nameof(definition));
        }

        if (itemSeparator.Length > 1_024)
        {
            throw new ArgumentException(
                L("Loc.Output.Error.SeparatorLength", "항목 구분자는 1,024자 이하여야 합니다."),
                nameof(definition));
        }

        if (prefix.Length > SelectionOutputFormatDefaults.MaximumAffixCharacters
            || suffix.Length > SelectionOutputFormatDefaults.MaximumAffixCharacters)
        {
            throw new ArgumentException(
                L("Loc.Output.Validation.AffixTooLong", "전체 결과의 앞·뒤 문구는 각각 16,384자 이하여야 합니다."),
                nameof(definition));
        }

        if (profileId is { Length: > 128 })
        {
            throw new ArgumentException(
                L("Loc.Output.Error.ProfileIdLength", "프로필 ID는 128자 이하여야 합니다."),
                nameof(definition));
        }

        return new SelectionOutputFormatDefinition(
            id,
            displayName,
            template,
            itemSeparator,
            profileId,
            prefix,
            suffix);
    }

    private string L(string key, string koreanFallback) =>
        _localizer?.Get(key, koreanFallback) ?? koreanFallback;

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
