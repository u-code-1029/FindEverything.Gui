using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public enum ProfileEditorMode
{
    Guided,
    Expert,
}

public enum GuidedDateSourcePreset
{
    SingleValue,
    YearAndMonthDay,
    YearMonthAndDay,
}

public enum GuidedSourcePart
{
    Value,
    Year,
    MonthDay,
    Month,
    Day,
}

/// <summary>
/// A clickable portion of a sample path.  Positions are measured in the
/// normalized (forward-slash separated) sample so untouched text can remain a
/// literal when the guided template is rebuilt.
/// </summary>
public sealed record GuidedPathChoiceViewModel(
    int SegmentIndex,
    int Start,
    int Length,
    string Value,
    string DisplayName,
    string Description,
    bool IsWholeSegment);

public sealed class GuidedPathSegmentViewModel
{
    public GuidedPathSegmentViewModel(
        int index,
        string prefix,
        string value,
        GuidedPathChoiceViewModel wholeChoice,
        IReadOnlyList<GuidedPathChoiceViewModel> partChoices)
    {
        Index = index;
        Prefix = prefix;
        Value = value;
        WholeChoice = wholeChoice;
        PartChoices = partChoices;
    }

    public int Index { get; }

    public string Prefix { get; }

    public string Value { get; }

    public GuidedPathChoiceViewModel WholeChoice { get; }

    public IReadOnlyList<GuidedPathChoiceViewModel> PartChoices { get; }

    public bool HasPartChoices => PartChoices.Count > 1;
}

public sealed record GuidedPathAssignmentViewModel(
    GuidedSourcePart Part,
    string GroupName,
    GuidedPathChoiceViewModel Choice)
{
    public string Summary => $"{SourcePartDisplayName(Part)}: {Choice.Description}";

    public static string SourcePartDisplayName(GuidedSourcePart part) => part switch
    {
        GuidedSourcePart.Year => "연도",
        GuidedSourcePart.MonthDay => "월일",
        GuidedSourcePart.Month => "월",
        GuidedSourcePart.Day => "일",
        _ => "값",
    };
}

/// <summary>
/// An editable field row in the declarative profile editor.
/// </summary>
public partial class ProfileFieldDraftViewModel : ObservableObject
{
    private bool _isInitializing;

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _fieldId = string.Empty;

    [ObservableProperty]
    private string _groupName = string.Empty;

    [ObservableProperty]
    private string _groupNamesText = string.Empty;

    [ObservableProperty]
    private ProfileFieldValueKind _kind = ProfileFieldValueKind.String;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _parseFormat = string.Empty;

    [ObservableProperty]
    private string _displayFormat = string.Empty;

    [ObservableProperty]
    private GuidedDateSourcePreset _dateSourcePreset = GuidedDateSourcePreset.SingleValue;

    [ObservableProperty]
    private bool _isTerminalField;

    public ObservableCollection<GuidedPathAssignmentViewModel> GuidedAssignments { get; } = [];

    public string KindDisplayName => Kind switch
    {
        ProfileFieldValueKind.String => "텍스트",
        ProfileFieldValueKind.Int32 => "정수",
        ProfileFieldValueKind.Decimal => "소수",
        ProfileFieldValueKind.DateTime => "날짜/시간",
        ProfileFieldValueKind.Boolean => "참/거짓",
        _ => Kind.ToString(),
    };

    public string TemplateActionLabel => string.IsNullOrWhiteSpace(Header)
        ? "경로 조각 선택"
        : $"‘{Header}’에 넣을 경로 조각 선택";

    public bool IsDateTime => Kind == ProfileFieldValueKind.DateTime;

    public bool UsesSingleDateSource =>
        !IsDateTime || DateSourcePreset == GuidedDateSourcePreset.SingleValue;

    public bool UsesYearAndMonthDay =>
        IsDateTime && DateSourcePreset == GuidedDateSourcePreset.YearAndMonthDay;

    public bool UsesYearMonthAndDay =>
        IsDateTime && DateSourcePreset == GuidedDateSourcePreset.YearMonthAndDay;

    public bool HasGuidedAssignments => GuidedAssignments.Count > 0;

    public bool SupportsGuidedAssignments =>
        TryInferGuidedDateSourcePreset(out _);

    public string SourceSummary
    {
        get
        {
            if (GuidedAssignments.Count > 0)
            {
                return string.Join(" · ", GuidedAssignments
                    .OrderBy(static assignment => assignment.Part)
                    .Select(static assignment => assignment.Summary));
            }

            var groups = EffectiveGroupNames;
            return groups.Count == 0
                ? "아직 경로 조각을 연결하지 않았습니다."
                : $"정규식 그룹: {string.Join(" + ", groups)}";
        }
    }

    public IReadOnlyList<string> EffectiveGroupNames
    {
        get
        {
            var groupNames = ParseGroupNames(GroupNamesText);
            if (groupNames.Count > 0)
            {
                return groupNames;
            }

            return string.IsNullOrWhiteSpace(GroupName)
                ? []
                : [GroupName.Trim()];
        }
    }

    public ProfileFieldDraftViewModel()
    {
    }

    public ProfileFieldDraftViewModel(ProfileFieldManifest field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _isInitializing = true;
        try
        {
            Header = field.Header ?? string.Empty;
            FieldId = field.FieldId ?? string.Empty;
            GroupName = field.GroupName ?? string.Empty;
            GroupNamesText = string.Join(", ", field.GroupNames ?? []);
            Kind = field.Kind;
            Required = field.Required;
            ParseFormat = field.ParseFormat ?? string.Empty;
            DisplayFormat = field.DisplayFormat ?? string.Empty;
            DateSourcePreset = TryInferGuidedDateSourcePreset(out var preset)
                ? preset
                : GuidedDateSourcePreset.SingleValue;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnKindChanged(ProfileFieldValueKind value)
    {
        OnPropertyChanged(nameof(KindDisplayName));
        OnPropertyChanged(nameof(IsDateTime));
        OnPropertyChanged(nameof(UsesSingleDateSource));
        OnPropertyChanged(nameof(UsesYearAndMonthDay));
        OnPropertyChanged(nameof(UsesYearMonthAndDay));
        OnPropertyChanged(nameof(SupportsGuidedAssignments));
    }

    partial void OnHeaderChanged(string value) =>
        OnPropertyChanged(nameof(TemplateActionLabel));

    partial void OnFieldIdChanged(string value)
    {
        OnPropertyChanged(nameof(TemplateActionLabel));
    }

    partial void OnGroupNameChanged(string value) => NotifySourceStateChanged();

    partial void OnGroupNamesTextChanged(string value) => NotifySourceStateChanged();

    partial void OnDateSourcePresetChanged(GuidedDateSourcePreset value)
    {
        if (_isInitializing)
        {
            OnPropertyChanged(nameof(UsesSingleDateSource));
            OnPropertyChanged(nameof(UsesYearAndMonthDay));
            OnPropertyChanged(nameof(UsesYearMonthAndDay));
            return;
        }

        GuidedAssignments.Clear();
        ConfigureGroupsForPreset();
        if (IsDateTime)
        {
            ParseFormat = "yyyyMMdd";
            DisplayFormat = "yyyy-MM-dd";
        }

        OnPropertyChanged(nameof(UsesSingleDateSource));
        OnPropertyChanged(nameof(UsesYearAndMonthDay));
        OnPropertyChanged(nameof(UsesYearMonthAndDay));
        OnPropertyChanged(nameof(SupportsGuidedAssignments));
        NotifySourceStateChanged();
    }

    public void SynchronizeGuidedDateSourcePreset()
    {
        if (!TryInferGuidedDateSourcePreset(out var preset))
        {
            return;
        }

        _isInitializing = true;
        try
        {
            DateSourcePreset = preset;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    public void EnsureDateDefaults()
    {
        if (!IsDateTime)
        {
            return;
        }

        ParseFormat = string.IsNullOrWhiteSpace(ParseFormat) ? "yyyyMMdd" : ParseFormat;
        DisplayFormat = string.IsNullOrWhiteSpace(DisplayFormat) ? "yyyy-MM-dd" : DisplayFormat;
        ConfigureGroupsForPreset();
    }

    public void SetGuidedAssignment(
        GuidedSourcePart part,
        GuidedPathChoiceViewModel choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ConfigureGroupsForPreset();
        var groupName = GroupNameFor(part);
        var existing = GuidedAssignments.FirstOrDefault(assignment => assignment.Part == part);
        if (existing is not null)
        {
            GuidedAssignments.Remove(existing);
        }

        GuidedAssignments.Add(new GuidedPathAssignmentViewModel(part, groupName, choice));
        NotifySourceStateChanged();
    }

    public void RemoveAssignmentsOverlapping(GuidedPathChoiceViewModel choice)
    {
        var end = choice.Start + choice.Length;
        var removed = false;
        for (var index = GuidedAssignments.Count - 1; index >= 0; index--)
        {
            var candidate = GuidedAssignments[index].Choice;
            var candidateEnd = candidate.Start + candidate.Length;
            if (candidate.Start < end && choice.Start < candidateEnd)
            {
                GuidedAssignments.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            NotifySourceStateChanged();
        }
    }

    public void ClearGuidedAssignments()
    {
        if (GuidedAssignments.Count == 0)
        {
            return;
        }

        GuidedAssignments.Clear();
        NotifySourceStateChanged();
    }

    public bool HasAssignment(GuidedSourcePart part) =>
        GuidedAssignments.Any(assignment => assignment.Part == part);

    public static List<string> ParseGroupNames(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static groupName => groupName.Length > 0)
                .ToList();

    private void ConfigureGroupsForPreset()
    {
        if (!IsDateTime || DateSourcePreset == GuidedDateSourcePreset.SingleValue)
        {
            GroupName = string.IsNullOrWhiteSpace(GroupName)
                ? CreateGroupName(FieldId, null)
                : GroupName.Trim();
            GroupNamesText = string.Empty;
            return;
        }

        var expectedParts = DateSourcePreset == GuidedDateSourcePreset.YearAndMonthDay
            ? new[] { GuidedSourcePart.Year, GuidedSourcePart.MonthDay }
            : new[] { GuidedSourcePart.Year, GuidedSourcePart.Month, GuidedSourcePart.Day };
        var existing = ParseGroupNames(GroupNamesText);
        if (existing.Count != expectedParts.Length)
        {
            existing = expectedParts
                .Select(part => CreateGroupName(FieldId, part))
                .ToList();
        }

        GroupName = string.Empty;
        GroupNamesText = string.Join(", ", existing);
    }

    private string GroupNameFor(GuidedSourcePart part)
    {
        if (part == GuidedSourcePart.Value)
        {
            return string.IsNullOrWhiteSpace(GroupName)
                ? CreateGroupName(FieldId, null)
                : GroupName.Trim();
        }

        var parts = DateSourcePreset == GuidedDateSourcePreset.YearAndMonthDay
            ? new[] { GuidedSourcePart.Year, GuidedSourcePart.MonthDay }
            : new[] { GuidedSourcePart.Year, GuidedSourcePart.Month, GuidedSourcePart.Day };
        var index = Array.IndexOf(parts, part);
        var groups = ParseGroupNames(GroupNamesText);
        return index >= 0 && index < groups.Count
            ? groups[index]
            : CreateGroupName(FieldId, part);
    }

    private static string CreateGroupName(string fieldId, GuidedSourcePart? part)
    {
        var source = string.IsNullOrWhiteSpace(fieldId) ? "field" : fieldId.Trim();
        var characters = source
            .Select(static character => char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '_')
            .ToArray();
        var normalized = new string(characters);
        if (normalized.Length == 0 || !(char.IsLetter(normalized[0]) || normalized[0] == '_'))
        {
            normalized = $"field_{normalized}";
        }

        return part switch
        {
            GuidedSourcePart.Year => $"{normalized}Year",
            GuidedSourcePart.MonthDay => $"{normalized}MonthDay",
            GuidedSourcePart.Month => $"{normalized}Month",
            GuidedSourcePart.Day => $"{normalized}Day",
            _ => normalized,
        };
    }

    private void NotifySourceStateChanged()
    {
        OnPropertyChanged(nameof(EffectiveGroupNames));
        OnPropertyChanged(nameof(HasGuidedAssignments));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SupportsGuidedAssignments));
    }

    private bool TryInferGuidedDateSourcePreset(out GuidedDateSourcePreset preset)
    {
        var groups = EffectiveGroupNames;
        if (groups.Count <= 1)
        {
            preset = GuidedDateSourcePreset.SingleValue;
            return true;
        }

        if (!IsDateTime)
        {
            preset = GuidedDateSourcePreset.SingleValue;
            return false;
        }

        if (groups.Count == 2
            && HasSemanticSuffix(groups[0], "year")
            && HasSemanticSuffix(groups[1], "monthDay"))
        {
            preset = GuidedDateSourcePreset.YearAndMonthDay;
            return true;
        }

        if (groups.Count == 3
            && HasSemanticSuffix(groups[0], "year")
            && HasSemanticSuffix(groups[1], "month")
            && HasSemanticSuffix(groups[2], "day"))
        {
            preset = GuidedDateSourcePreset.YearMonthAndDay;
            return true;
        }

        preset = GuidedDateSourcePreset.SingleValue;
        return false;
    }

    private static bool HasSemanticSuffix(string groupName, string semanticName)
    {
        if (string.Equals(groupName, semanticName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var suffix = char.ToUpperInvariant(semanticName[0]) + semanticName[1..];
        return groupName.EndsWith(suffix, StringComparison.Ordinal);
    }
}

/// <summary>
/// An editable regular-expression row in the declarative profile editor.
/// </summary>
public partial class ProfileRuleDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private ProfileRegexMatchMode _matchMode = ProfileRegexMatchMode.Full;

    [ObservableProperty]
    private bool _ignoreCase = true;

    [ObservableProperty]
    private int _timeoutMilliseconds = 100;

    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private string _pathTemplate = string.Empty;

    [ObservableProperty]
    private string _stopTraversalGroupsText = string.Empty;

    public string MatchModeDisplayName => MatchMode switch
    {
        ProfileRegexMatchMode.Full => "전체 일치",
        ProfileRegexMatchMode.Partial => "부분 일치",
        _ => MatchMode.ToString(),
    };

    public ProfileRuleDraftViewModel()
    {
    }

    public ProfileRuleDraftViewModel(ProfileRegexRuleManifest rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Id = rule.Id ?? string.Empty;
        MatchMode = rule.MatchMode;
        IgnoreCase = rule.IgnoreCase;
        TimeoutMilliseconds = rule.TimeoutMilliseconds;
        Pattern = rule.Pattern ?? string.Empty;
        PathTemplate = rule.PathTemplate ?? string.Empty;
        StopTraversalGroupsText = string.Join(", ", rule.StopTraversalWhenCapturedGroups ?? []);
    }

    partial void OnMatchModeChanged(ProfileRegexMatchMode value) =>
        OnPropertyChanged(nameof(MatchModeDisplayName));
}

/// <summary>
/// A single value produced by testing an example path in the profile editor.
/// </summary>
public sealed class ProfileTestResultViewModel
{
    public ProfileTestResultViewModel(string fieldName, string? value, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(status);

        FieldName = fieldName;
        Value = string.IsNullOrEmpty(value) ? "—" : value;
        Status = status;
    }

    public string FieldName { get; }

    public string Value { get; }

    public string Status { get; }
}
