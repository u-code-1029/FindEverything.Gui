using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

/// <summary>
/// An editable field row in the declarative profile editor.
/// </summary>
public partial class ProfileFieldDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _fieldId = string.Empty;

    [ObservableProperty]
    private string _groupName = string.Empty;

    [ObservableProperty]
    private ProfileFieldValueKind _kind = ProfileFieldValueKind.String;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _parseFormat = string.Empty;

    [ObservableProperty]
    private string _displayFormat = string.Empty;

    public string KindDisplayName => Kind switch
    {
        ProfileFieldValueKind.String => "텍스트",
        ProfileFieldValueKind.Int32 => "정수",
        ProfileFieldValueKind.Decimal => "소수",
        ProfileFieldValueKind.DateTime => "날짜/시간",
        ProfileFieldValueKind.Boolean => "참/거짓",
        _ => Kind.ToString(),
    };

    public ProfileFieldDraftViewModel()
    {
    }

    public ProfileFieldDraftViewModel(ProfileFieldManifest field)
    {
        ArgumentNullException.ThrowIfNull(field);

        Header = field.Header ?? string.Empty;
        FieldId = field.FieldId ?? string.Empty;
        GroupName = field.GroupName ?? string.Empty;
        Kind = field.Kind;
        Required = field.Required;
        ParseFormat = field.ParseFormat ?? string.Empty;
        DisplayFormat = field.DisplayFormat ?? string.Empty;
    }

    partial void OnKindChanged(ProfileFieldValueKind value) =>
        OnPropertyChanged(nameof(KindDisplayName));
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
