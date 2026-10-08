using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

internal static class ProfileEditorText
{
    public static string Get(string key, string koreanFallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as string
        ?? koreanFallback;

    public static string Format(
        string key,
        string koreanFallback,
        params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, koreanFallback), arguments);
}

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

/// <summary>
/// A beginner-friendly date input choice. The example is what users see in a
/// path; <see cref="Format"/> is the exact format persisted to the profile.
/// </summary>
public sealed record GuidedDateFormatChoice(string Format, string DisplayName);

public enum GuidedSourcePart
{
    Value,
    Year,
    MonthDay,
    Month,
    Day,
}

public partial class ProfileValueMappingDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _display = string.Empty;

    public ProfileValueMappingDraftViewModel()
    {
    }

    public ProfileValueMappingDraftViewModel(ProfileValueMappingManifest mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        Source = mapping.Source ?? string.Empty;
        Display = mapping.Display ?? string.Empty;
    }
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
        GuidedSourcePart.Year => ProfileEditorText.Get("Loc.Profiles.SourcePart.Year", "연도"),
        GuidedSourcePart.MonthDay => ProfileEditorText.Get("Loc.Profiles.SourcePart.MonthDay", "월일"),
        GuidedSourcePart.Month => ProfileEditorText.Get("Loc.Profiles.SourcePart.Month", "월"),
        GuidedSourcePart.Day => ProfileEditorText.Get("Loc.Profiles.SourcePart.Day", "일"),
        _ => ProfileEditorText.Get("Loc.Profiles.SourcePart.Value", "값"),
    };
}

/// <summary>
/// An editable field row in the declarative profile editor.
/// </summary>
public partial class ProfileFieldDraftViewModel : ObservableObject
{
    private static readonly IReadOnlyList<GuidedDateFormatChoice> CommonDateFormats =
    [
        new("yyyyMMdd", ProfileEditorText.Get("Loc.Profiles.DateFormat.Digits", "20260521  ·  숫자 8자리")),
        new("yyyy-MM-dd", ProfileEditorText.Get("Loc.Profiles.DateFormat.Hyphen", "2026-05-21  ·  하이픈(-)")),
        new("yyyy_MM_dd", ProfileEditorText.Get("Loc.Profiles.DateFormat.Underscore", "2026_05_21  ·  밑줄(_)")),
        new("yyyy.MM.dd", ProfileEditorText.Get("Loc.Profiles.DateFormat.Dot", "2026.05.21  ·  점(.)")),
        new("yyyy MM dd", ProfileEditorText.Get("Loc.Profiles.DateFormat.Space", "2026 05 21  ·  공백")),
        new("yyyy년 MM월 dd일", ProfileEditorText.Get("Loc.Profiles.DateFormat.Korean", "2026년 05월 21일  ·  한글")),
        new("MM-dd-yyyy", ProfileEditorText.Get("Loc.Profiles.DateFormat.MonthDayYear", "05-21-2026  ·  월-일-연도")),
        new("dd-MM-yyyy", ProfileEditorText.Get("Loc.Profiles.DateFormat.DayMonthYear", "21-05-2026  ·  일-월-연도")),
        new("yyyyMMdd_HHmmss", ProfileEditorText.Get("Loc.Profiles.DateFormat.DateTime", "20260521_143005  ·  날짜와 시간")),
    ];

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
    [NotifyPropertyChangedFor(nameof(DateFormatChoices))]
    private string _parseFormat = string.Empty;

    [ObservableProperty]
    private string _displayFormat = string.Empty;

    [ObservableProperty]
    private GuidedDateSourcePreset _dateSourcePreset = GuidedDateSourcePreset.SingleValue;

    [ObservableProperty]
    private bool _isTerminalField;

    public ObservableCollection<GuidedPathAssignmentViewModel> GuidedAssignments { get; } = [];

    public ObservableCollection<ProfileValueMappingDraftViewModel> ValueMappings { get; } = [];

    public string KindDisplayName => Kind switch
    {
        ProfileFieldValueKind.String => ProfileEditorText.Get("Loc.Profiles.Kind.Text", "텍스트"),
        ProfileFieldValueKind.Int32 => ProfileEditorText.Get("Loc.Profiles.Kind.Integer", "정수"),
        ProfileFieldValueKind.Decimal => ProfileEditorText.Get("Loc.Profiles.Kind.Decimal", "소수"),
        ProfileFieldValueKind.DateTime => ProfileEditorText.Get("Loc.Profiles.Kind.DateTime", "날짜/시간"),
        ProfileFieldValueKind.Boolean => ProfileEditorText.Get("Loc.Profiles.Kind.Boolean", "참/거짓"),
        _ => Kind.ToString(),
    };

    public string TemplateActionLabel => string.IsNullOrWhiteSpace(Header)
        ? ProfileEditorText.Get("Loc.Profiles.Action.SelectSegment", "경로 조각 선택")
        : ProfileEditorText.Format(
            "Loc.Profiles.Action.SelectSegmentForField",
            "‘{0}’에 넣을 경로 조각 선택",
            Header);

    public bool IsDateTime => Kind == ProfileFieldValueKind.DateTime;

    public bool UsesSingleDateSource =>
        !IsDateTime || DateSourcePreset == GuidedDateSourcePreset.SingleValue;

    public bool UsesYearAndMonthDay =>
        IsDateTime && DateSourcePreset == GuidedDateSourcePreset.YearAndMonthDay;

    public bool UsesYearMonthAndDay =>
        IsDateTime && DateSourcePreset == GuidedDateSourcePreset.YearMonthAndDay;

    public bool HasGuidedAssignments => GuidedAssignments.Count > 0;

    public bool CanConfigureValueMappings => Kind == ProfileFieldValueKind.String;

    public bool HasValueMappings => ValueMappings.Count > 0;

    // Keep an existing mapping table reachable after the field kind changes.
    // Validation intentionally rejects mappings on non-string fields, so hiding
    // these rows would leave the user with no way to remove the invalid data.
    public bool ShowValueMappingEditor => CanConfigureValueMappings || HasValueMappings;

    public bool HasAliasGroup => EffectiveGroupNames.Any(static groupName =>
        string.Equals(groupName, "alias", StringComparison.OrdinalIgnoreCase));

    public string ValueMappingDescription => HasAliasGroup
        ? ProfileEditorText.Get(
            "Loc.Profiles.Mapping.AliasDescription",
            "alias 그룹의 원본 코드를 사람이 읽기 쉬운 값으로 바꿔 표시합니다. 원본은 보존됩니다.")
        : ProfileEditorText.Get(
            "Loc.Profiles.Mapping.Description",
            "캡처한 원본 코드와 화면에 보여 줄 값을 연결합니다. 원본은 보존됩니다.");

    /// <summary>
    /// Known formats are described with concrete path examples so guided-mode
    /// users never need to know the .NET custom date format syntax. If a draft
    /// came from expert mode with a custom format, keep it selectable without
    /// rewriting the value.
    /// </summary>
    public IReadOnlyList<GuidedDateFormatChoice> DateFormatChoices
    {
        get
        {
            var choices = DateSourcePreset == GuidedDateSourcePreset.SingleValue
                ? CommonDateFormats
                : CommonDateFormats.Take(1).ToArray();
            var current = ParseFormat?.Trim() ?? string.Empty;
            if (current.Length == 0
                || choices.Any(choice =>
                    string.Equals(choice.Format, current, StringComparison.Ordinal)))
            {
                return choices;
            }

            return
            [
                .. choices,
                new(
                    current,
                    ProfileEditorText.Format(
                        "Loc.Profiles.DateFormat.KeepCustom",
                        "현재 사용자 지정 형식 유지  ·  {0}",
                        current)),
            ];
        }
    }

    public string DateFormatHelpText => DateSourcePreset switch
    {
        GuidedDateSourcePreset.YearAndMonthDay =>
            ProfileEditorText.Get(
                "Loc.Profiles.Date.Help.YearMonthDay",
                "선택한 연도와 월일을 붙인 모습과 같은 예시를 고르세요. 예: 2026 + 0521 → 20260521"),
        GuidedDateSourcePreset.YearMonthAndDay =>
            ProfileEditorText.Get(
                "Loc.Profiles.Date.Help.YearMonthAndDay",
                "선택한 연도, 월, 일을 붙인 모습과 같은 예시를 고르세요. 예: 2026 + 05 + 21 → 20260521"),
        _ => ProfileEditorText.Get(
            "Loc.Profiles.Date.Help.Single",
            "경로에서 날짜가 보이는 모습과 같은 예시를 고르세요."),
    };

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
                ? ProfileEditorText.Get(
                    "Loc.Profiles.Source.None",
                    "아직 경로 조각을 연결하지 않았습니다.")
                : ProfileEditorText.Format(
                    "Loc.Profiles.Source.RegexGroups",
                    "정규식 그룹: {0}",
                    string.Join(" + ", groups));
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
        InitializeValueMappings();
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
            foreach (var mapping in field.ValueMappings ?? [])
            {
                if (mapping is null)
                {
                    continue;
                }

                ValueMappings.Add(new ProfileValueMappingDraftViewModel(mapping));
            }
            DateSourcePreset = TryInferGuidedDateSourcePreset(out var preset)
                ? preset
                : GuidedDateSourcePreset.SingleValue;
        }
        finally
        {
            _isInitializing = false;
        }

        InitializeValueMappings();
    }

    partial void OnKindChanged(ProfileFieldValueKind value)
    {
        if (!_isInitializing)
        {
            if (value != ProfileFieldValueKind.DateTime)
            {
                // parseFormat is a DateTime-only contract. Do not rebuild capture
                // groups here: expert-mode composite fields must retain their
                // explicit group order when the value kind changes.
                ParseFormat = string.Empty;
            }

            if (value is ProfileFieldValueKind.String or ProfileFieldValueKind.Boolean)
            {
                DisplayFormat = string.Empty;
            }
            else if (value is ProfileFieldValueKind.Int32 or ProfileFieldValueKind.Decimal
                     && DisplayFormat is "yyyy-MM-dd" or "yyyy-MM-dd HH:mm:ss")
            {
                // These are the guided editor's automatic date display defaults,
                // not useful number formats. Preserve every other numeric format.
                DisplayFormat = string.Empty;
            }
        }

        OnPropertyChanged(nameof(KindDisplayName));
        OnPropertyChanged(nameof(IsDateTime));
        OnPropertyChanged(nameof(UsesSingleDateSource));
        OnPropertyChanged(nameof(UsesYearAndMonthDay));
        OnPropertyChanged(nameof(UsesYearMonthAndDay));
        OnPropertyChanged(nameof(DateFormatChoices));
        OnPropertyChanged(nameof(DateFormatHelpText));
        OnPropertyChanged(nameof(SupportsGuidedAssignments));
        OnPropertyChanged(nameof(CanConfigureValueMappings));
        OnPropertyChanged(nameof(ShowValueMappingEditor));
        AddValueMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnHeaderChanged(string value) =>
        OnPropertyChanged(nameof(TemplateActionLabel));

    partial void OnFieldIdChanged(string value)
    {
        OnPropertyChanged(nameof(TemplateActionLabel));
    }

    partial void OnGroupNameChanged(string value)
    {
        NotifySourceStateChanged();
        NotifyValueMappingDescriptionChanged();
    }

    partial void OnGroupNamesTextChanged(string value)
    {
        NotifySourceStateChanged();
        NotifyValueMappingDescriptionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddValueMapping))]
    private void AddValueMapping()
    {
        var mapping = new ProfileValueMappingDraftViewModel();
        ValueMappings.Add(mapping);
    }

    [RelayCommand]
    private void RemoveValueMapping(ProfileValueMappingDraftViewModel? mapping)
    {
        if (mapping is not null)
        {
            ValueMappings.Remove(mapping);
        }
    }

    private bool CanAddValueMapping() =>
        CanConfigureValueMappings
        && ValueMappings.Count < ProfileManifestLimits.MaximumValueMappingCount;

    private void InitializeValueMappings()
    {
        ValueMappings.CollectionChanged += OnValueMappingsCollectionChanged;
        foreach (var mapping in ValueMappings)
        {
            mapping.PropertyChanged += OnValueMappingPropertyChanged;
        }

        AddValueMappingCommand.NotifyCanExecuteChanged();
    }

    private void OnValueMappingsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
        {
            foreach (ProfileValueMappingDraftViewModel mapping in eventArgs.OldItems)
            {
                mapping.PropertyChanged -= OnValueMappingPropertyChanged;
            }
        }

        if (eventArgs.NewItems is not null)
        {
            foreach (ProfileValueMappingDraftViewModel mapping in eventArgs.NewItems)
            {
                mapping.PropertyChanged += OnValueMappingPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasValueMappings));
        OnPropertyChanged(nameof(ShowValueMappingEditor));
        OnPropertyChanged(nameof(ValueMappings));
        AddValueMappingCommand.NotifyCanExecuteChanged();
    }

    private void OnValueMappingPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
        OnPropertyChanged(nameof(ValueMappings));

    private void NotifyValueMappingDescriptionChanged()
    {
        OnPropertyChanged(nameof(HasAliasGroup));
        OnPropertyChanged(nameof(ValueMappingDescription));
    }

    partial void OnParseFormatChanged(string value)
    {
        if (_isInitializing || !IsDateTime)
        {
            return;
        }

        if (string.Equals(value, "yyyyMMdd_HHmmss", StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(DisplayFormat)
                || string.Equals(DisplayFormat, "yyyy-MM-dd", StringComparison.Ordinal)))
        {
            DisplayFormat = "yyyy-MM-dd HH:mm:ss";
        }
        else if (!string.Equals(value, "yyyyMMdd_HHmmss", StringComparison.Ordinal)
                 && string.Equals(DisplayFormat, "yyyy-MM-dd HH:mm:ss", StringComparison.Ordinal))
        {
            DisplayFormat = "yyyy-MM-dd";
        }
    }

    partial void OnDateSourcePresetChanged(GuidedDateSourcePreset value)
    {
        if (_isInitializing)
        {
            OnPropertyChanged(nameof(UsesSingleDateSource));
            OnPropertyChanged(nameof(UsesYearAndMonthDay));
            OnPropertyChanged(nameof(UsesYearMonthAndDay));
            OnPropertyChanged(nameof(DateFormatChoices));
            OnPropertyChanged(nameof(DateFormatHelpText));
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
        OnPropertyChanged(nameof(DateFormatChoices));
        OnPropertyChanged(nameof(DateFormatHelpText));
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
        SuggestDateFormatFromAssignments();
        NotifySourceStateChanged();
    }

    public bool TryValidateGuidedDateSample(out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!IsDateTime || GuidedAssignments.Count == 0)
        {
            return true;
        }

        var requiredParts = RequiredDateSourceParts();
        var selectedValues = new List<string>(requiredParts.Length);
        foreach (var requiredPart in requiredParts)
        {
            var assignment = GuidedAssignments.FirstOrDefault(candidate => candidate.Part == requiredPart);
            if (assignment is null)
            {
                // The template compiler reports incomplete field connections.
                return true;
            }

            selectedValues.Add(assignment.Choice.Value);
        }

        var sample = string.Concat(selectedValues).Trim();
        var format = ParseFormat?.Trim() ?? string.Empty;
        bool isValid;
        try
        {
            isValid = format.Length > 0
                ? TryParseExactDate(sample, format)
                : DateTime.TryParse(
                    sample,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out _);
        }
        catch (FormatException)
        {
            isValid = false;
        }

        if (isValid)
        {
            return true;
        }

        var fieldName = string.IsNullOrWhiteSpace(Header) ? FieldId : Header;
        errorMessage = ProfileEditorText.Format(
            "Loc.Profiles.Date.Error.Invalid",
            "‘{0}’의 선택 값 ‘{1}’은(는) 선택한 날짜 모양으로 읽을 수 없습니다. 경로 속 날짜 모양을 다시 선택하세요.",
            fieldName,
            sample);
        return false;
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

    private void SuggestDateFormatFromAssignments()
    {
        if (!IsDateTime)
        {
            return;
        }

        var requiredParts = RequiredDateSourceParts();
        var selectedValues = new List<string>(requiredParts.Length);
        foreach (var requiredPart in requiredParts)
        {
            var assignment = GuidedAssignments.FirstOrDefault(candidate => candidate.Part == requiredPart);
            if (assignment is null)
            {
                return;
            }

            selectedValues.Add(assignment.Choice.Value);
        }

        var sample = string.Concat(selectedValues);
        var currentFormat = ParseFormat?.Trim() ?? string.Empty;
        if (currentFormat.Length > 0
            && TryParseExactDate(sample.Trim(), currentFormat))
        {
            // The current selection can be intentional. This is especially
            // important for ambiguous values such as 05-06-2026, which can be
            // either month-day or day-month depending on the user's choice.
            return;
        }

        foreach (var choice in CommonDateFormats)
        {
            if (TryParseExactDate(sample.Trim(), choice.Format))
            {
                ParseFormat = choice.Format;
                return;
            }
        }
    }

    private static bool TryParseExactDate(string value, string format)
    {
        try
        {
            return DateTime.TryParseExact(
                value,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private GuidedSourcePart[] RequiredDateSourceParts() => DateSourcePreset switch
    {
        GuidedDateSourcePreset.YearAndMonthDay =>
            [GuidedSourcePart.Year, GuidedSourcePart.MonthDay],
        GuidedDateSourcePreset.YearMonthAndDay =>
            [GuidedSourcePart.Year, GuidedSourcePart.Month, GuidedSourcePart.Day],
        _ => [GuidedSourcePart.Value],
    };

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
        ProfileRegexMatchMode.Full => ProfileEditorText.Get("Loc.Profiles.Match.FullShort", "전체 일치"),
        ProfileRegexMatchMode.Partial => ProfileEditorText.Get("Loc.Profiles.Match.Partial", "부분 일치"),
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
/// An editable rule that prevents direct discovery from entering a directory.
/// Only the directory's leaf name is supplied to this regular expression.
/// </summary>
public partial class ProfileDirectoryNameExclusionRuleDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private ProfileRegexMatchMode _matchMode = ProfileRegexMatchMode.Full;

    [ObservableProperty]
    private bool _ignoreCase = true;

    [ObservableProperty]
    private int _timeoutMilliseconds = 100;

    public string MatchModeDisplayName => MatchMode switch
    {
        ProfileRegexMatchMode.Full => ProfileEditorText.Get("Loc.Profiles.Match.FolderFull", "폴더 이름 전체 일치"),
        ProfileRegexMatchMode.Partial => ProfileEditorText.Get("Loc.Profiles.Match.FolderPartial", "폴더 이름 일부 일치"),
        _ => MatchMode.ToString(),
    };

    public ProfileDirectoryNameExclusionRuleDraftViewModel()
    {
    }

    public ProfileDirectoryNameExclusionRuleDraftViewModel(
        ProfileDirectoryNameExclusionRuleManifest rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Id = rule.Id ?? string.Empty;
        Pattern = rule.Pattern ?? string.Empty;
        MatchMode = rule.MatchMode;
        IgnoreCase = rule.IgnoreCase;
        TimeoutMilliseconds = rule.TimeoutMilliseconds;
    }

    partial void OnMatchModeChanged(ProfileRegexMatchMode value) =>
        OnPropertyChanged(nameof(MatchModeDisplayName));
}

/// <summary>
/// A string field populated from the first matching text file directly inside a
/// matched directory. The regular expression receives only the file name.
/// </summary>
public partial class ProfileTextFileFieldDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _fieldId = string.Empty;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _fileNamePattern = string.Empty;

    [ObservableProperty]
    private ProfileRegexMatchMode _matchMode = ProfileRegexMatchMode.Full;

    [ObservableProperty]
    private bool _ignoreCase = true;

    [ObservableProperty]
    private int _timeoutMilliseconds = 100;

    [ObservableProperty]
    private long _maxBytes = ProfileManifestLimits.DefaultTextFileMaximumBytes;

    public string MatchModeDisplayName => MatchMode switch
    {
        ProfileRegexMatchMode.Full => ProfileEditorText.Get("Loc.Profiles.Match.FileFull", "파일 이름 전체 일치"),
        ProfileRegexMatchMode.Partial => ProfileEditorText.Get("Loc.Profiles.Match.FilePartial", "파일 이름 일부 일치"),
        _ => MatchMode.ToString(),
    };

    public ProfileTextFileFieldDraftViewModel()
    {
    }

    public ProfileTextFileFieldDraftViewModel(ProfileTextFileFieldManifest field)
    {
        ArgumentNullException.ThrowIfNull(field);

        Header = field.Header ?? string.Empty;
        FieldId = field.FieldId ?? string.Empty;
        Order = field.Order;
        Required = field.Required;
        FileNamePattern = field.FileNamePattern ?? string.Empty;
        MatchMode = field.MatchMode;
        IgnoreCase = field.IgnoreCase;
        TimeoutMilliseconds = field.TimeoutMilliseconds;
        MaxBytes = field.MaxBytes;
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
        : this(fieldName, value, status, rawValue: null, hasMappedValue: false)
    {
    }

    public ProfileTestResultViewModel(
        string fieldName,
        string? value,
        string status,
        string? rawValue,
        bool hasMappedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(status);

        FieldName = fieldName;
        Value = string.IsNullOrEmpty(value) ? "—" : value;
        Status = status;
        RawValue = string.IsNullOrEmpty(rawValue) ? "—" : rawValue;
        HasMappedValue = hasMappedValue;
    }

    public string FieldName { get; }

    public string Value { get; }

    public string Status { get; }

    public string RawValue { get; }

    public bool HasMappedValue { get; }
}
