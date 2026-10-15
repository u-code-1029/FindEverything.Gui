using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    [ObservableProperty]
    private ProfileFieldDraftViewModel? _selectedGuidedField;

    public string CurrentStepTitle => CurrentEditorStep switch
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
