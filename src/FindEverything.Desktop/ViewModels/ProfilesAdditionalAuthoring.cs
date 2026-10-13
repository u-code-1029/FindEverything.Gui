using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    [ObservableProperty] private string _draftMaxScanDepthText = "256";
    public ObservableCollection<ProfileChildDirectoryFieldDraftViewModel> DraftChildDirectoryFields { get; } = [];
    public ObservableCollection<ProfileSelectionRuleDraftViewModel> DraftSelectionRules { get; } = [];
    public IReadOnlyList<string> AvailableSelectionFieldIds => DraftFields.Select(column => column.FieldId)
        .Concat(DraftTextFileFields.Select(column => column.FieldId)).Concat(DraftUserInputFields.Select(column => column.FieldId))
        .Concat(DraftChildDirectoryFields.Select(column => column.FieldId)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<SettingChoice<ProfileSelectionInputKind>> SelectionInputChoices =>
    [ new(ProfileSelectionInputKind.TextLines, L("Loc.Profiles.Selection.Lines", "한 줄에 값 하나")),
      new(ProfileSelectionInputKind.Table, L("Loc.Profiles.Selection.Table", "표 / CSV / Excel")),
      new(ProfileSelectionInputKind.Regex, L("Loc.Profiles.Selection.Regex", "정규식 그룹 (전문가)")) ];
    public IReadOnlyList<SettingChoice<ProfileSelectionValueSource>> SelectionValueSourceChoices =>
    [ new(ProfileSelectionValueSource.Displayed, L("Loc.Profiles.Selection.Displayed", "화면에 보이는 값")),
      new(ProfileSelectionValueSource.Original, L("Loc.Profiles.Selection.Original", "원본 값")) ];
    public IReadOnlyList<SettingChoice<ProfileSelectionDateComparison>> SelectionDateComparisonChoices =>
    [ new(ProfileSelectionDateComparison.DateOnly, L("Loc.Profiles.Selection.DateOnly", "날짜만 비교")),
      new(ProfileSelectionDateComparison.Exact, L("Loc.Profiles.Selection.ExactDate", "시각까지 비교")) ];
    public string ScanDepthValidationMessage => ProfileNumericInput.TryInt(DraftMaxScanDepthText, 0, 1024, out _)
        ? string.Empty : L("Loc.Profiles.ScanDepth.Invalid", "최대 탐색 깊이는 0–1024인 정수를 입력하세요.");
    partial void OnDraftMaxScanDepthTextChanged(string value) => MarkDraftChanged();
    private void ResetAdditionalDraft()
    {
        DraftChildDirectoryFields.Clear(); DraftSelectionRules.Clear(); DraftMaxScanDepthText = "256";
    }
    private void DetachAdditionalRows()
    {
        foreach (var row in DraftChildDirectoryFields) row.PropertyChanged -= OnAdditionalDraftChanged;
        foreach (var row in DraftSelectionRules) row.PropertyChanged -= OnAdditionalDraftChanged;
    }
    private void OnAdditionalDraftChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { OnPropertyChanged(nameof(AvailableSelectionFieldIds)); MarkDraftChanged(); }
    private void AddChildDirectoryFieldRow(ProfileChildDirectoryFieldDraftViewModel row)
    { row.PropertyChanged += OnAdditionalDraftChanged; DraftChildDirectoryFields.Add(row); MarkDraftChanged(); }
    private void AddSelectionRuleRow(ProfileSelectionRuleDraftViewModel row)
    { row.PropertyChanged += OnAdditionalDraftChanged; DraftSelectionRules.Add(row); MarkDraftChanged(); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddChildDirectoryField()
    {
        if (DraftChildDirectoryFields.Count >= ProfileManifestLimits.MaximumChildDirectoryFieldCount) return;
        var suffix = 1; while (AvailableSelectionFieldIds.Contains($"folder-exists-{suffix}", StringComparer.OrdinalIgnoreCase)) suffix++;
        AddChildDirectoryFieldRow(new() { FieldId = $"folder-exists-{suffix}", Header = L("Loc.Profiles.ChildDirectory.New", "폴더 있음"), DirectoryName = "Complete",
            OrderText = ((DraftFields.Count + DraftTextFileFields.Count + DraftUserInputFields.Count + DraftChildDirectoryFields.Count + 1) * 10)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveChildDirectoryField(ProfileChildDirectoryFieldDraftViewModel? row)
    { if (row is not null && DraftChildDirectoryFields.Remove(row)) { row.PropertyChanged -= OnAdditionalDraftChanged; MarkDraftChanged(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddSelectionRule()
    {
        if (DraftSelectionRules.Count >= ProfileSelectionMatchRules.MaximumRules) return;
        var suffix = 1; while (DraftSelectionRules.Any(rule => rule.Id == $"select-{suffix}")) suffix++;
        var rule = new ProfileSelectionRuleDraftViewModel { Id = $"select-{suffix}", DisplayName = L("Loc.Profiles.Selection.New", "붙여넣기 선택 규칙") };
        rule.Columns.Add(new() { FieldId = AvailableSelectionFieldIds.FirstOrDefault() ?? string.Empty, InputColumnIndexText = "1" });
        AddSelectionRuleRow(rule);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveSelectionRule(ProfileSelectionRuleDraftViewModel? rule)
    { if (rule is not null && DraftSelectionRules.Remove(rule)) { rule.PropertyChanged -= OnAdditionalDraftChanged; MarkDraftChanged(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddSelectionColumn(ProfileSelectionRuleDraftViewModel? rule)
    {
        if (rule is null || rule.Columns.Count >= ProfileSelectionMatchRules.MaximumColumns) return;
        rule.Columns.Add(new() { FieldId = AvailableSelectionFieldIds.FirstOrDefault(id => rule.Columns.All(column => column.FieldId != id)) ?? string.Empty,
            InputColumnIndexText = (rule.Columns.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveSelectionColumn(ProfileSelectionColumnDraftViewModel? column)
    { if (column is null) return; foreach (var rule in DraftSelectionRules) if (rule.Columns.Remove(column)) break; }
}
