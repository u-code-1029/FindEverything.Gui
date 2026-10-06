using System.Globalization;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Filtering;
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

public sealed class CatalogItemViewModel
{
    private readonly string[] _searchableValues;

    public CatalogItemViewModel(
        CatalogItem item,
        IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fields);

        FullPath = item.FullPath;
        RelativePath = item.RelativePath;
        MatchedRuleId = item.MatchedRuleId;
        CoveragePending = item.CoveragePending;
        Values = item.Values;

        var displayValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var searchableValues = new List<string>(fields.Count + 2)
        {
            CoverageText,
        };
        foreach (var field in fields)
        {
            Values.TryGetValue(field.FieldId, out var value);
            var displayValue = FormatValue(value, field.DisplayFormat);
            displayValues[field.FieldId] = displayValue;
            searchableValues.Add(displayValue);
        }

        searchableValues.Add(FullPath);
        DisplayValues = displayValues;
        _searchableValues = searchableValues.ToArray();
    }

    public string FullPath { get; }

    public string RelativePath { get; }

    public string MatchedRuleId { get; }

    public bool CoveragePending { get; }

    public string CoverageText => CoveragePending ? "대기 범위" : "완료";

    public IReadOnlyDictionary<string, object?> Values { get; }

    public IReadOnlyDictionary<string, string> DisplayValues { get; }

    public bool Matches(string? filterText)
    {
        return TextFilter.MatchesAll(_searchableValues, filterText);
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
