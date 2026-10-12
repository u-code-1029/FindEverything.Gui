using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Filtering;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public sealed class ProfileChoiceViewModel(ILoadedProfile profile)
{
    public ILoadedProfile Profile { get; } = profile;

    public string Id => Profile.Descriptor.Id;

    public string DisplayName => Profile.Descriptor.DisplayName;

    public string Version => Profile.Descriptor.Version;

    public override string ToString() => DisplayName;
}

public sealed class CatalogItemViewModel : ObservableObject
{
    private readonly Dictionary<string, string> _rawDisplayValues;
    private readonly Dictionary<string, string> _mappedDisplayValues;
    private readonly Dictionary<string, object?> _rawOutputValues;
    private readonly Dictionary<string, object?> _mappedOutputValues;
    private readonly string[] _rawSearchableValues;
    private readonly string[] _mappedSearchableValues;
    private readonly string _coverageText;
    private bool _showOriginalValues;

    public CatalogItemViewModel(
        CatalogItem item,
        IReadOnlyList<ProfileFieldDescriptor> fields,
        IAppLocalizer localizer)
        : this(item, fields, localizer, null, null, null)
    {
    }

    public CatalogItemViewModel(
        CatalogItem item,
        IReadOnlyList<ProfileFieldDescriptor> fields,
        IAppLocalizer localizer,
        IReadOnlyDictionary<string, string>? persistedUserInputValues,
        Func<string, string, Task>? persistUserInput,
        Action? userInputChanged)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(localizer);

        FullPath = item.FullPath;
        RelativePath = item.RelativePath;
        MatchedRuleId = item.MatchedRuleId;
        CoveragePending = item.CoveragePending;
        Values = item.Values;
        _coverageText = CoveragePending
            ? localizer.Get("Loc.Common.PendingScope", "대기 범위")
            : localizer.Get("Loc.Common.Completed", "완료");

        var rawDisplayValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappedDisplayValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappedOutputValues = Values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var rawOutputValues = Values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var userInputValues = new Dictionary<string, CatalogUserInputValueViewModel>(
            StringComparer.OrdinalIgnoreCase);
        var mappedValueIndicators = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var mappedValueTooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawSearchableValues = new List<string>(fields.Count + 2)
        {
            CoverageText,
        };
        var mappedSearchableValues = new List<string>(fields.Count + 2)
        {
            CoverageText,
        };
        foreach (var field in fields)
        {
            if (field.SourceKind == ProfileFieldSourceKind.UserInput)
            {
                var initialValue = persistedUserInputValues is not null
                    && persistedUserInputValues.TryGetValue(field.FieldId, out var savedValue)
                        ? savedValue
                        : field.DefaultValue ?? string.Empty;
                CatalogUserInputValueViewModel? input = null;
                input = new CatalogUserInputValueViewModel(
                    field.FieldId,
                    field.Placeholder,
                    initialValue,
                    persistUserInput,
                    () =>
                    {
                        var current = input!;
                        rawDisplayValues[field.FieldId] = current.Value;
                        mappedDisplayValues[field.FieldId] = current.Value;
                        rawOutputValues[field.FieldId] = current.Value;
                        mappedOutputValues[field.FieldId] = current.Value;
                        OnPropertyChanged(nameof(DisplayValues));
                        OnPropertyChanged(nameof(OutputValues));
                        OnPropertyChanged(nameof(ExportDisplayValues));
                        userInputChanged?.Invoke();
                    });
                userInputValues[field.FieldId] = input;
                rawDisplayValues[field.FieldId] = initialValue;
                mappedDisplayValues[field.FieldId] = initialValue;
                rawOutputValues[field.FieldId] = initialValue;
                mappedOutputValues[field.FieldId] = initialValue;
                mappedValueIndicators[field.FieldId] = false;
                mappedValueTooltips[field.FieldId] = string.Empty;
                continue;
            }

            Values.TryGetValue(field.FieldId, out var value);
            var rawDisplayValue = FormatValue(value, field.DisplayFormat);
            var mappedDisplayValue = rawDisplayValue;
            var isMapped = false;
            if (value is string source
                && field.ValueMappings.TryGetValue(source, out var mappedValue)
                && mappedValue is not null)
            {
                mappedDisplayValue = mappedValue;
                isMapped = true;
            }

            rawDisplayValues[field.FieldId] = rawDisplayValue;
            mappedDisplayValues[field.FieldId] = mappedDisplayValue;
            mappedValueIndicators[field.FieldId] = isMapped;
            mappedValueTooltips[field.FieldId] = isMapped
                ? localizer.Format(
                    "Loc.Catalog.Alias.Tooltip",
                    "값 매핑: {0} → {1}",
                    rawDisplayValue,
                    mappedDisplayValue)
                : string.Empty;
            if (isMapped)
            {
                mappedOutputValues[field.FieldId] = mappedDisplayValue;
            }

            rawSearchableValues.Add(rawDisplayValue);
            mappedSearchableValues.Add(mappedDisplayValue);
        }

        rawSearchableValues.Add(FullPath);
        mappedSearchableValues.Add(FullPath);
        _rawDisplayValues = rawDisplayValues;
        _mappedDisplayValues = mappedDisplayValues;
        _rawOutputValues = rawOutputValues;
        _mappedOutputValues = mappedOutputValues;
        UserInputValues = userInputValues;
        MappedValueIndicators = mappedValueIndicators;
        MappedValueTooltips = mappedValueTooltips;
        HasMappedValues = mappedValueIndicators.Values.Any(static value => value);
        _rawSearchableValues = rawSearchableValues.ToArray();
        _mappedSearchableValues = mappedSearchableValues.ToArray();
    }

    public string FullPath { get; }

    public string RelativePath { get; }

    public string MatchedRuleId { get; }

    public bool CoveragePending { get; }

    public string CoverageText => _coverageText;

    public IReadOnlyDictionary<string, object?> Values { get; }

    public IReadOnlyDictionary<string, CatalogUserInputValueViewModel> UserInputValues { get; }

    public IReadOnlyDictionary<string, string> DisplayValues =>
        _showOriginalValues ? _rawDisplayValues : _mappedDisplayValues;

    public IReadOnlyDictionary<string, object?> OutputValues =>
        _showOriginalValues ? _rawOutputValues : _mappedOutputValues;

    public IReadOnlyDictionary<string, bool> MappedValueIndicators { get; }

    public IReadOnlyDictionary<string, string> MappedValueTooltips { get; }

    public bool HasMappedValues { get; }

    /// <summary>
    /// Gets the values written by tabular exports. This follows the same
    /// original/mapped presentation mode as the grid without exposing those
    /// presentation rules to the file writer.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExportDisplayValues => DisplayValues;

    public bool Matches(string? filterText)
    {
        return TextFilter.MatchesAll(
            (_showOriginalValues ? _rawSearchableValues : _mappedSearchableValues)
                .Concat(UserInputValues.Values.Select(static value => value.Value)),
            filterText);
    }

    public void SetShowOriginalValues(bool showOriginalValues)
    {
        if (_showOriginalValues == showOriginalValues)
        {
            return;
        }

        _showOriginalValues = showOriginalValues;
        OnPropertyChanged(nameof(DisplayValues));
        OnPropertyChanged(nameof(OutputValues));
        OnPropertyChanged(nameof(ExportDisplayValues));
    }

    private static string FormatValue(object? value, string? displayFormat)
    {
        if (value is null)
        {
            return "—";
        }

        try
        {
            return value is IFormattable formattable
                ? formattable.ToString(displayFormat, CultureInfo.CurrentCulture) ?? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }
        catch (FormatException)
        {
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }
    }
}

public sealed class CatalogUserInputValueViewModel : ObservableObject
{
    private readonly Func<string, string, Task>? _persist;
    private readonly Action _changed;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private string _value;
    private int _changeVersion;
    private int _savedVersion;

    public CatalogUserInputValueViewModel(
        string fieldId,
        string? placeholder,
        string value,
        Func<string, string, Task>? persist,
        Action changed)
    {
        FieldId = fieldId;
        Placeholder = placeholder ?? string.Empty;
        _value = value;
        _persist = persist;
        _changed = changed;
    }

    public string FieldId { get; }

    public string Placeholder { get; }

    public bool HasPendingSave =>
        Volatile.Read(ref _changeVersion) != Volatile.Read(ref _savedVersion);

    public string Value
    {
        get => _value;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _value, value))
            {
                return;
            }

            _changed();
            if (_persist is not null)
            {
                _ = Interlocked.Increment(ref _changeVersion);
                OnPropertyChanged(nameof(HasPendingSave));
                ScheduleSave();
            }
        }
    }

    public async Task CommitAsync()
    {
        _saveDebounce?.Cancel();
        _saveDebounce = null;
        var version = Volatile.Read(ref _changeVersion);
        if (_persist is not null && version != Volatile.Read(ref _savedVersion))
        {
            await PersistVersionAsync(version, Value).ConfigureAwait(false);
        }
    }

    private void ScheduleSave()
    {
        _saveDebounce?.Cancel();
        if (_persist is null)
        {
            _saveDebounce = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _saveDebounce = cancellation;
        _ = SaveAfterDelayAsync(
            cancellation,
            Volatile.Read(ref _changeVersion),
            Value);
    }

    private async Task SaveAfterDelayAsync(
        CancellationTokenSource cancellation,
        int version,
        string value)
    {
        try
        {
            await Task.Delay(400, cancellation.Token).ConfigureAwait(false);
            await PersistVersionAsync(version, value).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            // The owning view model reports the failure. Keep the version dirty
            // so LostFocus or shutdown can retry the latest value.
        }
        finally
        {
            if (ReferenceEquals(_saveDebounce, cancellation))
            {
                _saveDebounce = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task PersistVersionAsync(int version, string value)
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A cancelled debounce can race with an explicit LostFocus/shutdown
            // commit. Never let that older task overwrite a value that was typed
            // and queued later.
            if (version < Volatile.Read(ref _changeVersion))
            {
                return;
            }

            await _persist!(FieldId, value).ConfigureAwait(false);
            var observed = Volatile.Read(ref _savedVersion);
            while (version > observed)
            {
                var original = Interlocked.CompareExchange(
                    ref _savedVersion,
                    version,
                    observed);
                if (original == observed)
                {
                    break;
                }

                observed = original;
            }
        }
        finally
        {
            _persistGate.Release();
        }
    }
}
