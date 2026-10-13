using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Globalization;

namespace FindEverything.Profile.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ProfileSelectionInputKind>))]
public enum ProfileSelectionInputKind { TextLines, Table, Regex }
[JsonConverter(typeof(JsonStringEnumConverter<ProfileSelectionValueSource>))]
public enum ProfileSelectionValueSource { Displayed, Original }
[JsonConverter(typeof(JsonStringEnumConverter<ProfileSelectionDateComparison>))]
public enum ProfileSelectionDateComparison { DateOnly, Exact }

public sealed class ProfileSelectionMatchColumnManifest
{
    public string FieldId { get; set; } = string.Empty;
    public string? InputHeader { get; set; }
    /// <summary>Zero-based input table column. TextLines uses column zero.</summary>
    public int? InputColumnIndex { get; set; }
    public string? RegexGroup { get; set; }
    public ProfileSelectionValueSource ValueSource { get; set; } = ProfileSelectionValueSource.Displayed;
    public ProfileSelectionDateComparison DateComparison { get; set; } = ProfileSelectionDateComparison.DateOnly;
    public string? DateFormat { get; set; }
}

public sealed class ProfileSelectionMatchRuleManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProfileSelectionInputKind InputKind { get; set; } = ProfileSelectionInputKind.TextLines;
    public bool HasHeaderRow { get; set; } = true;
    /// <summary>Null/empty means infer TAB or comma; only a single separator is accepted.</summary>
    public string? Delimiter { get; set; }
    public string? RegexPattern { get; set; }
    public bool IgnoreCase { get; set; } = true;
    public bool Multiline { get; set; } = true;
    public int TimeoutMilliseconds { get; set; } = 250;
    public List<ProfileSelectionMatchColumnManifest> Columns { get; set; } = [];
}

public static class ProfileSelectionMatchRules
{
    public const int MaximumRules = 32;
    public const int MaximumColumns = 32;

    /// <summary>Declarative validation shared by the runtime and authoring UI.</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<ProfileSelectionMatchRuleManifest>? rules,
        IEnumerable<string> fieldIds)
    {
        var errors = new List<string>();
        var fields = new HashSet<string>(fieldIds, StringComparer.OrdinalIgnoreCase);
        var captured = rules?.ToArray() ?? [];
        if (captured.Length > MaximumRules) errors.Add($"Selection rules must not exceed {MaximumRules}.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in captured)
        {
            if (rule is null) { errors.Add("Selection rules cannot contain null entries."); continue; }
            var label = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.Id : rule.DisplayName;
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || !ids.Add(rule.Id))
                errors.Add($"Selection rule '{label}' needs a unique id (1–64 characters).");
            if (string.IsNullOrWhiteSpace(rule.DisplayName) || rule.DisplayName.Length > 128)
                errors.Add($"Selection rule '{label}' needs a display name (1–128 characters).");
            if (!Enum.IsDefined(rule.InputKind)) errors.Add($"Selection rule '{label}' has an invalid input kind.");
            if (rule.Delimiter is { Length: > 1 }) errors.Add($"Selection rule '{label}' delimiter must be one character.");
            if (rule.TimeoutMilliseconds is < 10 or > 2000) errors.Add($"Selection rule '{label}' regex timeout must be 10–2000ms.");
            if (rule.Columns is null || rule.Columns.Count is < 1 or > MaximumColumns)
            { errors.Add($"Selection rule '{label}' needs 1–{MaximumColumns} column mappings."); continue; }
            if (rule.InputKind == ProfileSelectionInputKind.TextLines && rule.Columns.Count != 1)
                errors.Add($"Selection rule '{label}' TextLines input maps exactly one column.");
            Regex? regex = null;
            if (rule.InputKind == ProfileSelectionInputKind.Regex)
            {
                if (string.IsNullOrWhiteSpace(rule.RegexPattern) || rule.RegexPattern.Length > 4096)
                    errors.Add($"Selection rule '{label}' needs a regex pattern (1–4096 characters).");
                else try { regex = new Regex(rule.RegexPattern, RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(Math.Clamp(rule.TimeoutMilliseconds, 10, 2000))); }
                catch (ArgumentException exception) { errors.Add($"Selection rule '{label}': {exception.Message}"); }
            }
            var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in rule.Columns)
            {
                if (column is null) { errors.Add($"Selection rule '{label}' has a null mapping."); continue; }
                if (!fields.Contains(column.FieldId) || !seenFields.Add(column.FieldId))
                    errors.Add($"Selection rule '{label}' field '{column.FieldId}' is missing or repeated.");
                if (!Enum.IsDefined(column.ValueSource) || !Enum.IsDefined(column.DateComparison))
                    errors.Add($"Selection rule '{label}' field '{column.FieldId}' has an invalid comparison.");
                if (column.InputColumnIndex is < 0 or > 16383) errors.Add($"Selection rule '{label}' column index must be 0–16383.");
                if (rule.InputKind == ProfileSelectionInputKind.Table && string.IsNullOrWhiteSpace(column.InputHeader)
                    && column.InputColumnIndex is null) errors.Add($"Selection rule '{label}' needs an input header or column index.");
                if (!string.IsNullOrWhiteSpace(column.InputHeader) && !rule.HasHeaderRow)
                    errors.Add($"Selection rule '{label}' requires a header row to use a named input column.");
                if (rule.InputKind == ProfileSelectionInputKind.Regex && (string.IsNullOrWhiteSpace(column.RegexGroup)
                    || (regex is not null && !regex.GetGroupNames().Contains(column.RegexGroup, StringComparer.Ordinal))))
                    errors.Add($"Selection rule '{label}' regex group '{column.RegexGroup}' is not defined.");
                if (column.DateFormat is { Length: > 128 }) errors.Add($"Selection rule '{label}' date format is too long.");
                else if (!string.IsNullOrWhiteSpace(column.DateFormat))
                    try { _ = DateTime.Now.ToString(column.DateFormat, CultureInfo.InvariantCulture); }
                    catch (FormatException exception) { errors.Add($"Selection rule '{label}' date format: {exception.Message}"); }
            }
        }
        return errors;
    }
}
