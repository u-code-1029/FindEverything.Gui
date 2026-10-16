using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    [ObservableProperty]
    private ProfileFieldDraftViewModel? _selectedGuidedField;

    public string CurrentStepTitle => GetEditorStepTitle(CurrentEditorStep);

    private string GetEditorStepTitle(ProfileEditorStep step) => step switch
    {
        ProfileEditorStep.Profile => L("Loc.Ux.Authoring.Step.Info", "1. 기본 정보"),
        ProfileEditorStep.PathAndFields => L("Loc.Ux.Authoring.Step.Path", "2. 경로와 컬럼"),
        ProfileEditorStep.Review => L("Loc.Ux.Authoring.Step.Review", "3. 확인과 저장"),
        ProfileEditorStep.DataSources => L("Loc.Ux.Authoring.Optional.Data", "선택 · 폴더와 파일 데이터"),
        ProfileEditorStep.SelectionRules => L("Loc.Ux.Authoring.Optional.Selection", "선택 · 외부 데이터로 선택"),
        _ => L("Loc.Ux.Authoring.Optional.Scan", "선택 · 탐색 범위와 제외 규칙"),
    };

    public bool IsOptionalEditorStep => CurrentEditorStep is ProfileEditorStep.DataSources
        or ProfileEditorStep.SelectionRules or ProfileEditorStep.ScanOptions;

    private (int Files, int Directories, int Inputs, int Mappings, int Selection, int Exclusions, int Stops, string Depth)? _optionalSummaryState;
    private string _optionalDataSummary = string.Empty;
    private string _optionalSelectionSummary = string.Empty;
    private string _optionalScanSummary = string.Empty;
    private int _configuredOptionalSectionCount;

    public string OptionalDataSummary => _optionalDataSummary;
    public string OptionalSelectionSummary => _optionalSelectionSummary;
    public string OptionalScanSummary => _optionalScanSummary;
    public int ConfiguredOptionalSectionCount => _configuredOptionalSectionCount;
    public string OptionalStepsButtonText => ConfiguredOptionalSectionCount == 0
        ? L("Loc.Ux.Authoring.Optional", "선택 설정")
        : F("Loc.Ux.Authoring.Optional.Configured", "선택 설정 · {0}", ConfiguredOptionalSectionCount);
    public string OptionalStepsSummary => string.Join(Environment.NewLine,
        L("Loc.Ux.Authoring.Optional.Summary.Help", "초안에 추가한 설정입니다. 유효성은 확인과 저장 단계에서 검증하세요."),
        OptionalDataSummary, OptionalSelectionSummary, OptionalScanSummary);

    private void RefreshOptionalStepSummaries()
    {
        if (_isPopulatingDraft) return;
        var state = (Files: DraftTextFileFields.Count, Directories: DraftChildDirectoryFields.Count,
            Inputs: DraftUserInputFields.Count, Mappings: DraftFields.Sum(static field => field.ValueMappings.Count),
            Selection: DraftSelectionRules.Count, Exclusions: DraftDirectoryExclusionRules.Count,
            Stops: IsGuidedMode ? (GuidedTerminalGroups()?.Count > 0 ? 1 : 0)
                : DraftRules.Count(static rule => ProfileFieldDraftViewModel.ParseGroupNames(rule.StopTraversalGroupsText).Count > 0),
            Depth: DraftMaxScanDepthText.Trim());
        // Typing values inside an existing rule must not re-render the step rail.
        if (_optionalSummaryState == state) return;
        _optionalSummaryState = state;
        SetProperty(ref _optionalDataSummary,
            F("Loc.Ux.Authoring.Optional.Data.Summary", "텍스트 파일 {0} · 하위 폴더 {1} · 직접 입력 {2} · 값 매핑 {3}",
                state.Files, state.Directories, state.Inputs, state.Mappings), nameof(OptionalDataSummary));
        SetProperty(ref _optionalSelectionSummary,
            F("Loc.Ux.Authoring.Optional.Selection.Summary", "외부 데이터 선택 규칙 {0}", state.Selection), nameof(OptionalSelectionSummary));
        SetProperty(ref _optionalScanSummary,
            F("Loc.Ux.Authoring.Optional.Scan.SummaryWithStops", "제외 규칙 {0} · 탐색 중단 규칙 {1} · 최대 탐색 깊이 {2}",
                state.Exclusions, state.Stops, state.Depth), nameof(OptionalScanSummary));
        var configured = (state.Files + state.Directories + state.Inputs + state.Mappings > 0 ? 1 : 0)
            + (state.Selection > 0 ? 1 : 0)
            + (state.Exclusions > 0 || state.Stops > 0 || state.Depth != "256" ? 1 : 0);
        if (SetProperty(ref _configuredOptionalSectionCount, configured, nameof(ConfiguredOptionalSectionCount)))
            OnPropertyChanged(nameof(OptionalStepsButtonText));
        OnPropertyChanged(nameof(OptionalStepsSummary));
    }

    partial void OnSelectedGuidedFieldChanged(ProfileFieldDraftViewModel? value)
    {
        if (!_isPopulatingDraft && IsPathAndFieldsStep && value is not null && !ReferenceEquals(value, _activeAssignmentField))
            BeginAssignment(value, NextMissingSourcePart(value) ?? FirstGuidedSourcePart(value));
    }

    private static GuidedSourcePart FirstGuidedSourcePart(ProfileFieldDraftViewModel field) =>
        field.IsDateTime && field.DateSourcePreset != GuidedDateSourcePreset.SingleValue
            ? GuidedSourcePart.Year : GuidedSourcePart.Value;

    [RelayCommand(CanExecute = nameof(CanValidateAndSave))]
    private async Task SaveAndTestAsync()
    {
        await SaveDraftAsync().ConfigureAwait(true);
        // Validation, cancellation, or a failed reload must never test an old
        // saved definition while suggesting that the user's draft was applied.
        if (EditorStatusSeverity == Wpf.Ui.Controls.InfoBarSeverity.Success && CanOpenSavedProfileInPlayground)
            OpenSavedProfileInPlayground();
    }

    /// <summary>Uses the normal draft-replacement protection when returning from a test.</summary>
    public async Task<bool> OpenForEditingAsync(string profileId)
    {
        if (IsEditorBusy) return false;
        if (string.Equals(_originalProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            CurrentEditorStep = ProfileEditorStep.PathAndFields;
            return true; // Preserve an already open draft, including unsaved changes.
        }
        await RefreshEditableProfilesAsync(profileId).ConfigureAwait(true);
        var editable = EditableProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (editable is null) return false;
        SelectedEditableProfile = editable;
        await LoadDraftAsync().ConfigureAwait(true);
        if (!string.Equals(_originalProfileId, profileId, StringComparison.OrdinalIgnoreCase)) return false;
        CurrentEditorStep = ProfileEditorStep.PathAndFields;
        return true;
    }
}
