using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfileChildDirectoryFieldDraftViewModel : ObservableObject
{
    [ObservableProperty] private string _fieldId = string.Empty;
    [ObservableProperty] private string _header = string.Empty;
    [ObservableProperty] private string _directoryName = string.Empty;
    [ObservableProperty] private bool _ignoreCase = true;
    [ObservableProperty] private string _orderText = "0";
    public string NumericValidationMessage => ProfileNumericInput.OrderError(OrderText);
    public ProfileChildDirectoryFieldDraftViewModel() { }
    public ProfileChildDirectoryFieldDraftViewModel(ProfileChildDirectoryFieldManifest field)
    {
        FieldId = field.FieldId ?? string.Empty; Header = field.Header ?? string.Empty;
        DirectoryName = field.DirectoryName ?? string.Empty; IgnoreCase = field.IgnoreCase;
        OrderText = field.Order.ToString(CultureInfo.InvariantCulture);
    }
    public ProfileChildDirectoryFieldManifest ToManifest() => new()
    {
        FieldId = FieldId.Trim(), Header = Header.Trim(), DirectoryName = DirectoryName,
        IgnoreCase = IgnoreCase, Order = int.Parse(OrderText, CultureInfo.InvariantCulture),
    };
    partial void OnOrderTextChanged(string value) => OnPropertyChanged(nameof(NumericValidationMessage));
}

public partial class ProfileSelectionColumnDraftViewModel : ObservableObject
{
    [ObservableProperty] private string _fieldId = string.Empty;
    [ObservableProperty] private string _inputHeader = string.Empty;
    [ObservableProperty] private string _inputColumnIndexText = string.Empty;
    [ObservableProperty] private string _regexPattern = string.Empty;
    [ObservableProperty] private string _regexGroup = string.Empty;
    [ObservableProperty] private ProfileSelectionValueSource _valueSource = ProfileSelectionValueSource.Displayed;
    [ObservableProperty] private ProfileSelectionDateComparison _dateComparison = ProfileSelectionDateComparison.DateOnly;
    [ObservableProperty] private string _dateFormat = string.Empty;
    public string NumericValidationMessage => InputColumnIndexText.Length == 0
        || ProfileNumericInput.TryInt(InputColumnIndexText, 1, 16384, out _) ? string.Empty
        : ProfileEditorText.Get("Loc.Profiles.Selection.IndexError", "입력 컬럼 번호는 1–16384인 정수여야 합니다.");
    public ProfileSelectionColumnDraftViewModel() { }
    public ProfileSelectionColumnDraftViewModel(ProfileSelectionMatchColumnManifest column)
    {
        FieldId = column.FieldId; InputHeader = column.InputHeader ?? string.Empty;
        InputColumnIndexText = column.InputColumnIndex.HasValue ? (column.InputColumnIndex.Value + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        RegexPattern = column.RegexPattern ?? string.Empty;
        RegexGroup = column.RegexGroup ?? string.Empty; ValueSource = column.ValueSource;
        DateComparison = column.DateComparison; DateFormat = column.DateFormat ?? string.Empty;
    }
    public ProfileSelectionMatchColumnManifest ToManifest() => new()
    {
        FieldId = FieldId, InputHeader = Optional(InputHeader),
        InputColumnIndex = InputColumnIndexText.Length == 0 ? null : int.Parse(InputColumnIndexText, CultureInfo.InvariantCulture) - 1,
        RegexPattern = string.IsNullOrWhiteSpace(RegexPattern) ? null : RegexPattern,
        RegexGroup = Optional(RegexGroup), ValueSource = ValueSource, DateComparison = DateComparison,
        DateFormat = Optional(DateFormat),
    };
    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    partial void OnInputColumnIndexTextChanged(string value) => OnPropertyChanged(nameof(NumericValidationMessage));
}

public partial class ProfileSelectionRuleDraftViewModel : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private ProfileSelectionInputKind _inputKind = ProfileSelectionInputKind.TextLines;
    [ObservableProperty] private bool _hasHeaderRow = true;
    [ObservableProperty] private string _delimiter = string.Empty;
    [ObservableProperty] private string _regexPattern = string.Empty;
    [ObservableProperty] private bool _ignoreCase = true;
    [ObservableProperty] private bool _multiline = true;
    [ObservableProperty] private string _timeoutMillisecondsText = "250";
    public ObservableCollection<ProfileSelectionColumnDraftViewModel> Columns { get; } = [];
    public bool IsRegex => InputKind == ProfileSelectionInputKind.Regex;
    public bool IsTable => InputKind == ProfileSelectionInputKind.Table;
    public string NumericValidationMessage => string.Join(Environment.NewLine,
        Columns.Select(column => column.NumericValidationMessage).Append(
            ProfileNumericInput.TryInt(TimeoutMillisecondsText, 10, 2000, out _) ? string.Empty
                : ProfileEditorText.Get("Loc.Profiles.Selection.TimeoutError", "선택 규칙 제한 시간은 10–2000ms여야 합니다."))
        .Where(message => message.Length > 0));
    public ProfileSelectionRuleDraftViewModel()
    {
        Columns.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (ProfileSelectionColumnDraftViewModel column in e.OldItems) column.PropertyChanged -= ColumnChanged;
            if (e.NewItems is not null) foreach (ProfileSelectionColumnDraftViewModel column in e.NewItems) column.PropertyChanged += ColumnChanged;
            OnPropertyChanged(nameof(Columns)); OnPropertyChanged(nameof(NumericValidationMessage));
        };
    }
    public ProfileSelectionRuleDraftViewModel(ProfileSelectionMatchRuleManifest rule) : this()
    {
        Id = rule.Id; DisplayName = rule.DisplayName; InputKind = rule.InputKind;
        HasHeaderRow = rule.HasHeaderRow; Delimiter = rule.Delimiter ?? string.Empty;
        RegexPattern = rule.RegexPattern ?? string.Empty; IgnoreCase = rule.IgnoreCase;
        Multiline = rule.Multiline; TimeoutMillisecondsText = rule.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        foreach (var column in rule.Columns) Columns.Add(new(column));
    }
    private void ColumnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { OnPropertyChanged(nameof(Columns)); OnPropertyChanged(nameof(NumericValidationMessage)); }
    public ProfileSelectionMatchRuleManifest ToManifest() => new()
    {
        Id = Id.Trim(), DisplayName = DisplayName.Trim(), InputKind = InputKind, HasHeaderRow = HasHeaderRow,
        Delimiter = string.IsNullOrEmpty(Delimiter) ? null : Delimiter, RegexPattern = RegexPattern,
        IgnoreCase = IgnoreCase, Multiline = Multiline,
        TimeoutMilliseconds = int.Parse(TimeoutMillisecondsText, CultureInfo.InvariantCulture),
        Columns = Columns.Select(column => column.ToManifest()).ToList(),
    };
    [RelayCommand]
    private void AddField(string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || Columns.Count >= ProfileSelectionMatchRules.MaximumColumns
            || Columns.Any(column => string.Equals(column.FieldId, fieldId, StringComparison.OrdinalIgnoreCase))) return;
        Columns.Add(new() { FieldId = fieldId, InputColumnIndexText = (Columns.Count + 1).ToString(CultureInfo.InvariantCulture) });
    }
    partial void OnInputKindChanged(ProfileSelectionInputKind value)
    { OnPropertyChanged(nameof(IsRegex)); OnPropertyChanged(nameof(IsTable)); }
    partial void OnTimeoutMillisecondsTextChanged(string value) => OnPropertyChanged(nameof(NumericValidationMessage));
}
