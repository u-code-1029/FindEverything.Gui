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
    private readonly IReadOnlyDictionary<string, string> _rawDisplayValues;
    private readonly IReadOnlyDictionary<string, string> _mappedDisplayValues;
    private readonly IReadOnlyDictionary<string, object?> _mappedOutputValues;
    private readonly string[] _rawSearchableValues;
    private readonly string[] _mappedSearchableValues;
    private readonly string _coverageText;
    private bool _showOriginalValues;

    public CatalogItemViewModel(
        CatalogItem item,
        IReadOnlyList<ProfileFieldDescriptor> fields,
        IAppLocalizer localizer)
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
        _mappedOutputValues = mappedOutputValues;
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

    public IReadOnlyDictionary<string, string> DisplayValues =>
        _showOriginalValues ? _rawDisplayValues : _mappedDisplayValues;

    public IReadOnlyDictionary<string, object?> OutputValues =>
        _showOriginalValues ? Values : _mappedOutputValues;

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
            _showOriginalValues ? _rawSearchableValues : _mappedSearchableValues,
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
