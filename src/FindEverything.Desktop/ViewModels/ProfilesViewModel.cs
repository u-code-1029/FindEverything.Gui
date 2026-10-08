using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public enum ProfileEditorStep
{
    Profile = 0,
    PathAndFields = 1,
    ScanOptions = 2,
    Review = 3,
}

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileCatalog _profileCatalog;
    private readonly IProfileAuthoringService _authoringService;
    private readonly IProfilePathTemplateCompiler _pathTemplateCompiler;
    private readonly IProfilePathCanonicalizer _pathCanonicalizer;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly IProfilePlaygroundNavigator _playgroundNavigator;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<ProfilesViewModel> _logger;
    private string? _originalProfileId;
    private string _draftVersion = "1.0.0";
    private bool _isPopulatingDraft;
    private string _analyzedNormalizedPath = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ProfileSummaryViewModel> _profiles = [];

    [ObservableProperty]
    private ProfileSummaryViewModel? _selectedProfile;

    [ObservableProperty]
    private IReadOnlyList<ProfileFieldInspectionViewModel> _fields = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileRuleInspectionViewModel> _rules = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileDirectoryNameExclusionRuleInspectionViewModel>
        _excludedDirectoryNameRules = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileTextFileFieldInspectionViewModel> _textFileFields = [];

    [ObservableProperty]
    private IReadOnlyList<PluginReportViewModel> _reports = [];

    [ObservableProperty]
    private IReadOnlyList<EditableProfileSummary> _editableProfiles = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDraftCommand))]
    private EditableProfileSummary? _selectedEditableProfile;

    [ObservableProperty]
    private string _draftDisplayName = string.Empty;

    [ObservableProperty]
    private string _draftId = string.Empty;

    [ObservableProperty]
    private bool _canEditDraftId = true;

    [ObservableProperty]
    private string _samplePath = string.Empty;

    [ObservableProperty]
    private ProfileEditorMode _editorMode = ProfileEditorMode.Guided;

    [ObservableProperty]
    private string _draftPathTemplate = string.Empty;

    [ObservableProperty]
    private string _generatedPatternPreview = string.Empty;

    [ObservableProperty]
    private string _templateStatusMessage = "실제 경로에서 변하는 부분을 결과 값으로 지정하세요.";

    [ObservableProperty]
    private InfoBarSeverity _templateStatusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseGuidedModeCommand))]
    private bool _canUseGuidedMode = true;

    [ObservableProperty]
    private string _editorStatusMessage = "기본 정보, 컬럼, 경로 규칙을 입력한 뒤 검증하세요.";

    [ObservableProperty]
    private InfoBarSeverity _editorStatusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isEditorBusy;

    [ObservableProperty]
    private IReadOnlyList<GuidedPathSegmentViewModel> _guidedPathSegments = [];

    [ObservableProperty]
    private bool _hasAnalyzedPath;

    [ObservableProperty]
    private bool _isAssignmentPickerOpen;

    [ObservableProperty]
    private string _assignmentPickerTitle = "먼저 결과 값에서 경로 조각 선택을 누르세요.";

    [ObservableProperty]
    private ProfileEditorStep _currentEditorStep = ProfileEditorStep.Profile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSavedProfileInPlaygroundCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenSavedProfileInPlayground))]
    private bool _isDraftDirty = true;

    private ProfileFieldDraftViewModel? _activeAssignmentField;

    private GuidedSourcePart _activeAssignmentPart = GuidedSourcePart.Value;

    public ProfilesViewModel(
        IProfileCatalog profileCatalog,
        IProfileAuthoringService authoringService,
        IProfilePathTemplateCompiler pathTemplateCompiler,
        IProfilePathCanonicalizer pathCanonicalizer,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IProfilePlaygroundNavigator playgroundNavigator,
        IAppLocalizer localizer,
        ILogger<ProfilesViewModel> logger)
    {
        _profileCatalog = profileCatalog;
        _authoringService = authoringService;
        _pathTemplateCompiler = pathTemplateCompiler;
        _pathCanonicalizer = pathCanonicalizer;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _playgroundNavigator = playgroundNavigator;
        _localizer = localizer;
        _logger = logger;

        FieldKindChoices =
        [
            new(ProfileFieldValueKind.String, L("Loc.Profiles.Kind.Text", "텍스트")),
            new(ProfileFieldValueKind.Int32, L("Loc.Profiles.Kind.Integer", "정수")),
            new(ProfileFieldValueKind.Decimal, L("Loc.Profiles.Kind.Decimal", "소수")),
            new(ProfileFieldValueKind.DateTime, L("Loc.Profiles.Kind.DateTime", "날짜/시간")),
            new(ProfileFieldValueKind.Boolean, L("Loc.Profiles.Kind.Boolean", "참/거짓")),
        ];
        MatchModeChoices =
        [
            new(ProfileRegexMatchMode.Full, L("Loc.Profiles.Match.Full", "전체 일치 (권장)")),
            new(ProfileRegexMatchMode.Partial, L("Loc.Profiles.Match.Partial", "부분 일치")),
        ];
        DateSourcePresetChoices =
        [
            new(GuidedDateSourcePreset.SingleValue, L("Loc.Profiles.Date.Single", "한 조각에서 날짜 읽기")),
            new(GuidedDateSourcePreset.YearAndMonthDay, L("Loc.Profiles.Date.YearMonthDay", "연도 + 월일")),
            new(GuidedDateSourcePreset.YearMonthAndDay, L("Loc.Profiles.Date.YearMonthAndDay", "연도 + 월 + 일")),
        ];
        TemplateStatusMessage = L(
            "Loc.Profiles.Status.TemplateInitial",
            "실제 경로에서 변하는 부분을 결과 값으로 지정하세요.");
        EditorStatusMessage = L(
            "Loc.Profiles.Status.EditorInitial",
            "기본 정보, 컬럼, 경로 규칙을 입력한 뒤 검증하세요.");
        AssignmentPickerTitle = L(
            "Loc.Profiles.Status.AssignmentInitial",
            "먼저 결과 값에서 경로 조각 선택을 누르세요.");
        ApplySnapshot(profileCatalog.Current, preferredProfileId: null);
        _profileCatalog.Changed += OnProfileCatalogChanged;
        NewDraft();
        _ = RefreshEditableProfilesAsync();
    }

    public IReadOnlyList<SettingChoice<ProfileFieldValueKind>> FieldKindChoices { get; }

    public IReadOnlyList<SettingChoice<ProfileRegexMatchMode>> MatchModeChoices { get; }

    public IReadOnlyList<SettingChoice<GuidedDateSourcePreset>> DateSourcePresetChoices { get; }

    public bool IsEditorReady => !IsEditorBusy;

    public bool IsGuidedMode => EditorMode == ProfileEditorMode.Guided;

    public bool IsExpertMode => EditorMode == ProfileEditorMode.Expert;

    public string EditorModeDisplay => EditorMode == ProfileEditorMode.Guided
        ? L("Loc.Profiles.EditorMode.Guided", "초보자 모드")
        : L("Loc.Profiles.EditorMode.Expert", "전문가 모드");

    public bool IsProfileStep => CurrentEditorStep == ProfileEditorStep.Profile;

    public bool IsPathAndFieldsStep => CurrentEditorStep == ProfileEditorStep.PathAndFields;

    public bool IsScanOptionsStep => CurrentEditorStep == ProfileEditorStep.ScanOptions;

    public bool IsReviewStep => CurrentEditorStep == ProfileEditorStep.Review;

    public bool CanGoToPreviousStep =>
        !IsEditorBusy && CurrentEditorStep > ProfileEditorStep.Profile;

    public bool CanGoToNextStep =>
        !IsEditorBusy && CurrentEditorStep < ProfileEditorStep.Review;

    public string EditorStepStatus => F(
        "Loc.Profiles.Step.Status",
        "{0}/4 단계",
        (int)CurrentEditorStep + 1);

    public bool CanOpenSavedProfileInPlayground =>
        !IsEditorBusy
        && !IsDraftDirty
        && _originalProfileId is { Length: > 0 } profileId
        && _profileCatalog.Current.TryGetProfile(profileId, out _);

    public ObservableCollection<ProfileFieldDraftViewModel> DraftFields { get; } = [];

    public ObservableCollection<ProfileRuleDraftViewModel> DraftRules { get; } = [];

    public ObservableCollection<ProfileDirectoryNameExclusionRuleDraftViewModel>
        DraftExcludedDirectoryNameRules { get; } = [];

    public ObservableCollection<ProfileTextFileFieldDraftViewModel>
        DraftTextFileFields { get; } = [];

    public event EventHandler? AssignmentPickerRequested;

    public event EventHandler? EditorStepChanged;

    partial void OnSelectedProfileChanged(ProfileSummaryViewModel? value)
    {
        Fields = value?.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .Select(field => new ProfileFieldInspectionViewModel(field, _localizer))
            .ToArray() ?? [];
        Rules = value?.Descriptor.Rules
            .OrderBy(static rule => rule.Order)
            .Select(rule => new ProfileRuleInspectionViewModel(rule, _localizer))
            .ToArray() ?? [];
        ExcludedDirectoryNameRules = value?.Descriptor.ExcludedDirectoryNameRules
            .OrderBy(static rule => rule.Order)
            .Select(static rule =>
                new ProfileDirectoryNameExclusionRuleInspectionViewModel(rule))
            .ToArray() ?? [];
        TextFileFields = value?.Descriptor.TextFileFields
            .OrderBy(static field => field.Order)
            .Select(static field => new ProfileTextFileFieldInspectionViewModel(field))
            .ToArray() ?? [];
    }

    partial void OnDraftDisplayNameChanged(string value) => MarkDraftChanged();

    partial void OnDraftIdChanged(string value) => MarkDraftChanged();

    partial void OnSamplePathChanged(string value)
    {
        if (_isPopulatingDraft)
        {
            return;
        }

        SetEditorStatus(
            L(
                "Loc.Profiles.Status.SampleChanged",
                "작성용 경로가 변경되었습니다. 2단계에서 경로 조각을 다시 분석하세요."),
            InfoBarSeverity.Informational);
    }

    partial void OnEditorModeChanged(ProfileEditorMode value)
    {
        _activeAssignmentField = null;
        IsAssignmentPickerOpen = false;
        OnPropertyChanged(nameof(IsGuidedMode));
        OnPropertyChanged(nameof(IsExpertMode));
        OnPropertyChanged(nameof(EditorModeDisplay));
    }

    partial void OnCurrentEditorStepChanged(ProfileEditorStep value)
    {
        OnPropertyChanged(nameof(IsProfileStep));
        OnPropertyChanged(nameof(IsPathAndFieldsStep));
        OnPropertyChanged(nameof(IsScanOptionsStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(CanGoToPreviousStep));
        OnPropertyChanged(nameof(CanGoToNextStep));
        OnPropertyChanged(nameof(EditorStepStatus));
        PreviousEditorStepCommand.NotifyCanExecuteChanged();
        NextEditorStepCommand.NotifyCanExecuteChanged();
        EditorStepChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnDraftPathTemplateChanged(string value)
    {
        if (_isPopulatingDraft)
        {
            return;
        }

        RefreshTemplatePreview();
        MarkDraftChanged();
    }

    partial void OnIsEditorBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditorReady));
        NewDraftCommand.NotifyCanExecuteChanged();
        LoadDraftCommand.NotifyCanExecuteChanged();
        ValidateDraftCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        BuildTemplateFromSampleCommand.NotifyCanExecuteChanged();
        UseExpertModeCommand.NotifyCanExecuteChanged();
        UseGuidedModeCommand.NotifyCanExecuteChanged();
        AddDirectoryExclusionRuleCommand.NotifyCanExecuteChanged();
        AddTextFileFieldCommand.NotifyCanExecuteChanged();
        PreviousEditorStepCommand.NotifyCanExecuteChanged();
        NextEditorStepCommand.NotifyCanExecuteChanged();
        OpenSavedProfileInPlaygroundCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoToPreviousStep));
        OnPropertyChanged(nameof(CanGoToNextStep));
        OnPropertyChanged(nameof(CanOpenSavedProfileInPlayground));
    }

    [RelayCommand]
    private void GoToEditorStep(ProfileEditorStep? step)
    {
        if (IsEditorBusy || step is null || !Enum.IsDefined(step.Value))
        {
            return;
        }

        CurrentEditorStep = step.Value;
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousEditorStep))]
    private void PreviousEditorStep()
    {
        CurrentEditorStep--;
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextEditorStep))]
    private void NextEditorStep()
    {
        CurrentEditorStep++;
    }

    private bool CanGoToPreviousEditorStep() => CanGoToPreviousStep;

    private bool CanGoToNextEditorStep() => CanGoToNextStep;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewDraft()
    {
        _isPopulatingDraft = true;
        try
        {
            DetachRows();
            _originalProfileId = null;
            CurrentEditorStep = ProfileEditorStep.Profile;
            _draftVersion = "1.0.0";
            CanEditDraftId = true;
            DraftDisplayName = L("Loc.Profiles.New.DefaultName", "새 프로필");
            DraftId = $"profile-{DateTime.Now:yyyyMMddHHmmss}";
            EditorMode = ProfileEditorMode.Guided;
            CanUseGuidedMode = true;
            _activeAssignmentField = null;
            IsAssignmentPickerOpen = false;
            DraftFields.Clear();
            DraftRules.Clear();
            DraftExcludedDirectoryNameRules.Clear();
            DraftTextFileFields.Clear();
            AddFieldRow(new ProfileFieldDraftViewModel
            {
                Header = L("Loc.Profiles.New.DefaultField", "이름"),
                FieldId = "name",
                GroupName = "name",
                Kind = ProfileFieldValueKind.String,
                Required = true,
            });
            DraftPathTemplate = "{name@name}";
            AddRuleRow(new ProfileRuleDraftViewModel
            {
                Id = "default",
                MatchMode = ProfileRegexMatchMode.Full,
                IgnoreCase = true,
                TimeoutMilliseconds = 100,
                Pattern = @"(?<name>[^\\/]+)",
                PathTemplate = "{name@name}",
            });
            SamplePath = @"C:\Example";
            AnalyzeSamplePathCore(SamplePath);
            if (GuidedPathSegments.Count > 0)
            {
                DraftFields[0].SetGuidedAssignment(
                    GuidedSourcePart.Value,
                    GuidedPathSegments[^1].WholeChoice);
            }
            RebuildGuidedTemplate();
            AddDirectoryExclusionRuleCommand.NotifyCanExecuteChanged();
            AddTextFileFieldCommand.NotifyCanExecuteChanged();
            SetEditorStatus(
                L(
                    "Loc.Profiles.Status.NewDraft",
                    "새 프로필 초안을 만들었습니다. 예제 값을 수정한 뒤 검증하세요."),
                InfoBarSeverity.Informational);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        RefreshTemplatePreview();
        IsDraftDirty = true;
        OpenSavedProfileInPlaygroundCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenSavedProfileInPlayground));
    }

    [RelayCommand(CanExecute = nameof(CanLoadDraft))]
    private async Task LoadDraftAsync()
    {
        if (SelectedEditableProfile is null)
        {
            return;
        }

        await RunEditorOperationAsync(async () =>
        {
            var manifest = await _authoringService.LoadAsync(SelectedEditableProfile)
                .ConfigureAwait(true);
            if (manifest is null)
            {
                throw new InvalidOperationException(L(
                    "Loc.Profiles.Error.NotEditable",
                    "선택한 파일은 GUI에서 편집할 수 있는 선언형 프로필이 아닙니다."));
            }

            PopulateDraft(manifest);
            SetEditorStatus(
                F(
                    "Loc.Profiles.Status.Opened",
                    "'{0}' 프로필을 열었습니다.",
                    DraftDisplayName),
                InfoBarSeverity.Informational);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BuildTemplateFromSampleAsync()
    {
        if (string.IsNullOrWhiteSpace(SamplePath))
        {
            TemplateStatusMessage = L(
                "Loc.Profiles.Status.PastePath",
                "먼저 실제 폴더 경로를 붙여 넣으세요.");
            TemplateStatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var samplePath = SamplePath.Trim();
        try
        {
            samplePath = _pathCanonicalizer.Canonicalize(samplePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Could not canonicalize the profile authoring path.");
            TemplateStatusMessage = L(
                "Loc.Profiles.Error.InvalidPath",
                "이 경로를 사용할 수 없습니다. 드라이브 또는 UNC 루트를 포함한 실제 절대 경로를 확인하세요.");
            TemplateStatusSeverity = InfoBarSeverity.Error;
            return;
        }

        SamplePath = samplePath;
        var normalizedSamplePath = samplePath.Replace('\\', '/');
        if (DraftFields.Any(static field => field.HasGuidedAssignments)
            && !string.Equals(
                normalizedSamplePath,
                _analyzedNormalizedPath,
                StringComparison.Ordinal))
        {
            var result = await _contentDialogService.ShowAsync(
                new ContentDialog
                {
                    Title = L("Loc.Profiles.Analyze.Title", "새 경로를 분석할까요?"),
                    Content = L(
                        "Loc.Profiles.Analyze.Message",
                        "현재 연결된 경로 조각이 초기화됩니다. 결과 값은 그대로 유지됩니다."),
                    PrimaryButtonText = L("Loc.Profiles.Analyze.Action", "새 경로 분석"),
                    CloseButtonText = L("Loc.Common.Cancel", "취소"),
                    DefaultButton = ContentDialogButton.Close,
                },
                CancellationToken.None).ConfigureAwait(true);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var hadGuidedAssignments = DraftFields.Any(static field => field.HasGuidedAssignments);
        var previousPathTemplate = DraftPathTemplate;

        _isPopulatingDraft = true;
        try
        {
            foreach (var field in DraftFields)
            {
                field.ClearGuidedAssignments();
            }

            AnalyzeSamplePathCore(samplePath);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        RebuildGuidedTemplate();
        if (hadGuidedAssignments
            || !string.Equals(
                previousPathTemplate,
                DraftPathTemplate,
                StringComparison.Ordinal))
        {
            MarkDraftChanged();
        }

        _activeAssignmentField = null;
        IsAssignmentPickerOpen = false;
        TemplateStatusMessage =
            L(
                "Loc.Profiles.Status.ChooseSegment",
                "결과 값 카드에서 경로 조각 선택을 누른 뒤, 아래의 폴더 또는 세부 조각 버튼을 클릭하세요.");
        TemplateStatusSeverity = InfoBarSeverity.Informational;
    }

    [RelayCommand]
    private void BeginAssignValue(ProfileFieldDraftViewModel? field) =>
        BeginAssignment(field, GuidedSourcePart.Value);

    [RelayCommand]
    private void BeginAssignYear(ProfileFieldDraftViewModel? field) =>
        BeginAssignment(field, GuidedSourcePart.Year);

    [RelayCommand]
    private void BeginAssignMonthDay(ProfileFieldDraftViewModel? field) =>
        BeginAssignment(field, GuidedSourcePart.MonthDay);

    [RelayCommand]
    private void BeginAssignMonth(ProfileFieldDraftViewModel? field) =>
        BeginAssignment(field, GuidedSourcePart.Month);

    [RelayCommand]
    private void BeginAssignDay(ProfileFieldDraftViewModel? field) =>
        BeginAssignment(field, GuidedSourcePart.Day);

    [RelayCommand]
    private void AssignPathChoice(GuidedPathChoiceViewModel? choice)
    {
        if (choice is null || _activeAssignmentField is null || !HasAnalyzedPath)
        {
            return;
        }

        foreach (var field in DraftFields)
        {
            field.RemoveAssignmentsOverlapping(choice);
        }

        _activeAssignmentField.SetGuidedAssignment(_activeAssignmentPart, choice);
        RebuildGuidedTemplate();

        var nextPart = NextMissingSourcePart(_activeAssignmentField);
        if (nextPart is null)
        {
            IsAssignmentPickerOpen = false;
            TemplateStatusMessage = F(
                "Loc.Profiles.Status.AssignmentCompleted",
                "‘{0}’ 연결을 완료했습니다.",
                _activeAssignmentField.Header);
            TemplateStatusSeverity = InfoBarSeverity.Success;
            _activeAssignmentField = null;
        }
        else
        {
            BeginAssignment(_activeAssignmentField, nextPart.Value);
        }

        MarkDraftChanged();
    }

    [RelayCommand]
    private void ClearFieldAssignments(ProfileFieldDraftViewModel? field)
    {
        if (field is null)
        {
            return;
        }

        field.ClearGuidedAssignments();
        if (ReferenceEquals(field, _activeAssignmentField))
        {
            _activeAssignmentField = null;
            IsAssignmentPickerOpen = false;
        }

        RebuildGuidedTemplate();
        MarkDraftChanged();
    }

    [RelayCommand]
    private void SelectTerminalField(ProfileFieldDraftViewModel? field)
    {
        foreach (var candidate in DraftFields)
        {
            candidate.IsTerminalField = ReferenceEquals(candidate, field);
        }

        MarkDraftChanged();
    }

    [RelayCommand]
    private void ClearTerminalField()
    {
        foreach (var field in DraftFields)
        {
            field.IsTerminalField = false;
        }

        MarkDraftChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSwitchToGuided))]
    private void UseGuidedMode()
    {
        if (IsGuidedMode)
        {
            return;
        }

        if (!TryResolveGuidedTemplate(out var template))
        {
            CanUseGuidedMode = false;
            OnPropertyChanged(nameof(IsGuidedMode));
            OnPropertyChanged(nameof(IsExpertMode));
            SetEditorStatus(
                L(
                    "Loc.Profiles.Status.GuidedUnavailable",
                    "이 정규식은 초보자 모드로 안전하게 바꿀 수 없습니다. 전문가 모드에서 계속 편집하세요."),
                InfoBarSeverity.Warning);
            return;
        }

        _isPopulatingDraft = true;
        try
        {
            _ = TryResolveGuidedTerminalField(DraftRules[0], out var terminalField);
            foreach (var field in DraftFields)
            {
                field.SynchronizeGuidedDateSourcePreset();
                field.IsTerminalField = ReferenceEquals(field, terminalField);
            }

            DraftPathTemplate = template;
            EditorMode = ProfileEditorMode.Guided;
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        RefreshTemplatePreview();
        SetEditorStatus(
            L("Loc.Profiles.Status.GuidedEnabled", "초보자 모드로 전환했습니다."),
            InfoBarSeverity.Informational);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void UseExpertMode()
    {
        if (IsExpertMode)
        {
            return;
        }

        if (!TryCompileTemplate(out var result))
        {
            ApplyTemplateErrors(result);
            OnPropertyChanged(nameof(IsGuidedMode));
            OnPropertyChanged(nameof(IsExpertMode));
            SetEditorStatus(
                L(
                    "Loc.Profiles.Status.FixTemplate",
                    "경로 템플릿 오류를 먼저 수정한 뒤 전문가 모드로 전환하세요."),
                InfoBarSeverity.Warning);
            return;
        }

        ReplaceRulesWithGuidedRule(result.Pattern!, DraftPathTemplate);
        EditorMode = ProfileEditorMode.Expert;
        CanUseGuidedMode = true;
        SetEditorStatus(
            L(
                "Loc.Profiles.Status.ExpertEnabled",
                "전문가 모드로 전환했습니다. 정규식을 직접 바꾸면 초보자 모드로 돌아갈 수 없습니다."),
            InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private void AddField()
    {
        var number = NextFieldNumber();
        AddFieldRow(new ProfileFieldDraftViewModel
        {
            Header = F("Loc.Profiles.New.FieldName", "결과 값 {0}", number),
            FieldId = $"field-{number}",
            GroupName = $"field{number}",
            Required = true,
        });
        if (IsGuidedMode)
        {
            RebuildGuidedTemplate();
        }
    }

    [RelayCommand]
    private void RemoveField(ProfileFieldDraftViewModel? field)
    {
        if (field is not null && DraftFields.Remove(field))
        {
            field.PropertyChanged -= OnDraftRowChanged;
            CloseAssignmentPickerFor(field);
            if (IsGuidedMode)
            {
                RebuildGuidedTemplate();
            }
            MarkDraftChanged();
        }
    }

    [RelayCommand]
    private void MoveFieldUp(ProfileFieldDraftViewModel? field)
    {
        if (Move(DraftFields, field, -1))
        {
            MarkDraftChanged();
        }
    }

    [RelayCommand]
    private void MoveFieldDown(ProfileFieldDraftViewModel? field)
    {
        if (Move(DraftFields, field, 1))
        {
            MarkDraftChanged();
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        if (DraftRules.Count >= ProfileManifestLimits.MaximumRegexRuleCount)
        {
            SetEditorStatus(
                F(
                    "Loc.Profiles.Error.RuleLimit",
                    "경로 정규식 규칙은 최대 {0}개까지 추가할 수 있습니다.",
                    ProfileManifestLimits.MaximumRegexRuleCount),
                InfoBarSeverity.Warning);
            return;
        }

        AddRuleRow(new ProfileRuleDraftViewModel
        {
            Id = $"rule-{DraftRules.Count + 1}",
            MatchMode = ProfileRegexMatchMode.Full,
            IgnoreCase = true,
            TimeoutMilliseconds = 100,
            Pattern = "^$",
        });
    }

    [RelayCommand]
    private void RemoveRule(ProfileRuleDraftViewModel? rule)
    {
        if (rule is not null && DraftRules.Remove(rule))
        {
            rule.PropertyChanged -= OnDraftRowChanged;
            MarkDraftChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddDirectoryExclusionRule))]
    private void AddDirectoryExclusionRule()
    {
        var suffix = 1;
        string id;
        do
        {
            id = $"exclude-{suffix++}";
        }
        while (DraftExcludedDirectoryNameRules.Any(rule =>
                   string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase)));

        AddDirectoryExclusionRuleRow(
            new ProfileDirectoryNameExclusionRuleDraftViewModel
            {
                Id = id,
                MatchMode = ProfileRegexMatchMode.Full,
                IgnoreCase = true,
                TimeoutMilliseconds = 100,
            });
        AddDirectoryExclusionRuleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveDirectoryExclusionRule(
        ProfileDirectoryNameExclusionRuleDraftViewModel? rule)
    {
        if (rule is not null && DraftExcludedDirectoryNameRules.Remove(rule))
        {
            rule.PropertyChanged -= OnDraftRowChanged;
            AddDirectoryExclusionRuleCommand.NotifyCanExecuteChanged();
            MarkDraftChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddTextFileField))]
    private void AddTextFileField()
    {
        var suffix = 1;
        string fieldId;
        do
        {
            fieldId = $"text-file-{suffix++}";
        }
        while (DraftFields.Any(field =>
                   string.Equals(field.FieldId, fieldId, StringComparison.OrdinalIgnoreCase))
               || DraftTextFileFields.Any(field =>
                   string.Equals(field.FieldId, fieldId, StringComparison.OrdinalIgnoreCase)));

        var nextOrder = DraftFields
            .Select(static (_, index) => (index + 1) * 10)
            .Concat(DraftTextFileFields.Select(static field => field.Order))
            .DefaultIfEmpty(0)
            .Max() + 10;
        AddTextFileFieldRow(new ProfileTextFileFieldDraftViewModel
        {
            Header = L("Loc.Profiles.New.TextFileField", "텍스트 파일 내용"),
            FieldId = fieldId,
            Order = nextOrder,
            FileNamePattern = @".*\.txt",
            MatchMode = ProfileRegexMatchMode.Full,
            IgnoreCase = true,
            TimeoutMilliseconds = 100,
            MaxBytes = ProfileManifestLimits.DefaultTextFileMaximumBytes,
        });
        AddTextFileFieldCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveTextFileField(ProfileTextFileFieldDraftViewModel? field)
    {
        if (field is not null && DraftTextFileFields.Remove(field))
        {
            field.PropertyChanged -= OnDraftRowChanged;
            AddTextFileFieldCommand.NotifyCanExecuteChanged();
            MarkDraftChanged();
        }
    }

    [RelayCommand]
    private void MoveRuleUp(ProfileRuleDraftViewModel? rule)
    {
        if (Move(DraftRules, rule, -1))
        {
            MarkDraftChanged();
        }
    }

    [RelayCommand]
    private void MoveRuleDown(ProfileRuleDraftViewModel? rule)
    {
        if (Move(DraftRules, rule, 1))
        {
            MarkDraftChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ValidateDraft()
    {
        try
        {
            var review = _authoringService.Validate(BuildManifest());
            ApplyReview(
                review,
                L(
                    "Loc.Profiles.Status.ValidationPassed",
                    "검증을 통과했습니다. 이제 프로필을 저장하세요."));
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Could not validate the profile draft.");
            SetEditorStatus(
                L(
                    "Loc.Profiles.Error.ValidationFailed",
                    "프로필 설정을 검증할 수 없습니다. 필수 입력값과 규칙을 확인하세요."),
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveDraftAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            SetEditorStatus(
                L(
                    "Loc.Profiles.Status.WaitToSave",
                    "인덱싱 또는 불러오기가 끝난 뒤 프로필을 저장하세요."),
                InfoBarSeverity.Warning);
            return;
        }

        await RunEditorOperationAsync(async () =>
        {
            ProfileSaveResult? result = null;
            try
            {
                var manifest = BuildManifest();
                var originalProfileId = _originalProfileId;
                await _operationCoordinator.RunAsync(ApplicationOperationKind.ProfileWrite, async cancellationToken =>
                {
                    result = await _authoringService.SaveAndApplyAsync(
                            manifest,
                            originalProfileId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }).ConfigureAwait(true);
            }
            catch (InvalidOperationException) when (_operationCoordinator.IsRunning)
            {
                SetEditorStatus(
                    L(
                        "Loc.Profiles.Status.WaitToSave",
                        "인덱싱 또는 불러오기가 끝난 뒤 프로필을 저장하세요."),
                    InfoBarSeverity.Warning);
                return;
            }

            if (result is null)
            {
                throw new InvalidOperationException(L(
                    "Loc.Profiles.Error.NoSaveResult",
                    "프로필 저장 결과를 받지 못했습니다."));
            }

            if (!result.Review.IsValid)
            {
                ApplyReview(result.Review, string.Empty);
                return;
            }

            if (!result.Applied)
            {
                var reloadMessages = result.Snapshot?.Reports
                    .Where(report => string.Equals(
                        report.ProfileId,
                        DraftId.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .SelectMany(static report => report.Diagnostics)
                    .Select(diagnostic =>
                        ProfileDiagnosticLocalizer.Translate(_localizer, diagnostic))
                    .Distinct()
                    .ToArray() ?? [];
                SetEditorStatus(
                    reloadMessages.Length == 0
                        ? L(
                            "Loc.Profiles.Error.SavedNotApplied",
                            "파일은 저장했지만 프로필을 적용하지 못했습니다. 로드 결과의 진단을 확인하세요.")
                        : string.Join(Environment.NewLine, reloadMessages),
                    InfoBarSeverity.Error);
                return;
            }

            _originalProfileId = result.Review.Profile!.Descriptor.Id;
            CanEditDraftId = false;
            IsDraftDirty = false;
            OpenSavedProfileInPlaygroundCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanOpenSavedProfileInPlayground));
            if (result.Snapshot is not null)
            {
                ApplySnapshot(result.Snapshot, _originalProfileId);
            }

            await RefreshEditableProfilesAsync(_originalProfileId).ConfigureAwait(true);
            SetEditorStatus(
                L(
                    "Loc.Profiles.Status.SavedApplied",
                    "프로필을 저장하고 카탈로그에 즉시 적용했습니다."),
                InfoBarSeverity.Success);
            _snackbarService.Show(
                L("Loc.Profiles.Save.Completed", "프로필 저장 완료"),
                F(
                    "Loc.Profiles.Save.Completed.Message",
                    "'{0}' 프로필을 바로 사용할 수 있습니다.",
                    result.Review.Profile.Descriptor.DisplayName),
                ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(4));
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSavedProfile))]
    private void OpenSavedProfileInPlayground()
    {
        if (_originalProfileId is null)
        {
            return;
        }

        _ = _playgroundNavigator.Navigate(_originalProfileId);
    }

    [RelayCommand]
    private void OpenUserProfilesFolder()
    {
        try
        {
            Directory.CreateDirectory(_authoringService.UserProfilesDirectory);
            _pathLauncher.OpenDirectory(_authoringService.UserProfilesDirectory);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open the user profiles directory.");
            SetEditorStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private bool CanEdit() => !IsEditorBusy;

    private bool CanLoadDraft() => !IsEditorBusy && SelectedEditableProfile is not null;

    private bool CanOpenSavedProfile() => CanOpenSavedProfileInPlayground;

    private bool CanSwitchToGuided() => !IsEditorBusy && CanUseGuidedMode;

    private bool CanAddDirectoryExclusionRule() =>
        !IsEditorBusy
        && DraftExcludedDirectoryNameRules.Count
            < ProfileManifestLimits.MaximumExcludedDirectoryNameRuleCount;

    private bool CanAddTextFileField() =>
        !IsEditorBusy
        && DraftTextFileFields.Count < ProfileManifestLimits.MaximumTextFileFieldCount;

    private ProfileManifest BuildManifest()
    {
        var fields = BuildFieldManifests();
        List<ProfileRegexRuleManifest> rules;
        if (IsGuidedMode)
        {
            foreach (var field in DraftFields)
            {
                if (!field.TryValidateGuidedDateSample(out var dateError))
                {
                    throw new InvalidOperationException(dateError);
                }
            }

            var compileResult = _pathTemplateCompiler.Compile(DraftPathTemplate, fields);
            if (!compileResult.IsValid || string.IsNullOrEmpty(compileResult.Pattern))
            {
                ApplyTemplateErrors(compileResult);
                throw new InvalidOperationException(BuildTemplateErrorMessage(compileResult));
            }

            rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    Pattern = compileResult.Pattern,
                    MatchMode = ProfileRegexMatchMode.Full,
                    IgnoreCase = true,
                    TimeoutMilliseconds = 100,
                    PathTemplate = DraftPathTemplate,
                    StopTraversalWhenCapturedGroups = GuidedTerminalGroups(),
                },
            ];
        }
        else
        {
            rules = DraftRules.Select(rule => new ProfileRegexRuleManifest
            {
                Id = rule.Id.Trim(),
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
                PathTemplate = GetRoundTripTemplate(rule, fields),
                StopTraversalWhenCapturedGroups = ParseNameList(rule.StopTraversalGroupsText),
            }).ToList();
        }

        return new ProfileManifest
        {
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = DraftId.Trim(),
            Version = _draftVersion,
            DisplayName = DraftDisplayName.Trim(),
            CandidateKind = ProfileCandidateKind.Directory,
            Fields = fields,
            TextFileFields = DraftTextFileFields.Count == 0
                ? null
                : DraftTextFileFields.Select(field => new ProfileTextFileFieldManifest
                {
                    FieldId = field.FieldId.Trim(),
                    Header = field.Header.Trim(),
                    Order = field.Order,
                    Required = field.Required,
                    FileNamePattern = field.FileNamePattern,
                    MatchMode = field.MatchMode,
                    IgnoreCase = field.IgnoreCase,
                    TimeoutMilliseconds = field.TimeoutMilliseconds,
                    MaxBytes = field.MaxBytes,
                }).ToList(),
            ExcludedDirectoryNameRules = DraftExcludedDirectoryNameRules.Count == 0
                ? null
                : DraftExcludedDirectoryNameRules.Select(rule =>
                    new ProfileDirectoryNameExclusionRuleManifest
                    {
                        Id = rule.Id.Trim(),
                        Pattern = rule.Pattern,
                        MatchMode = rule.MatchMode,
                        IgnoreCase = rule.IgnoreCase,
                        TimeoutMilliseconds = rule.TimeoutMilliseconds,
                    }).ToList(),
            Rules = rules,
        };
    }

    private void PopulateDraft(ProfileManifest manifest)
    {
        _isPopulatingDraft = true;
        try
        {
            DetachRows();
            _originalProfileId = manifest.Id?.Trim();
            CurrentEditorStep = ProfileEditorStep.Profile;
            _draftVersion = string.IsNullOrWhiteSpace(manifest.Version)
                ? "1.0.0"
                : manifest.Version.Trim();
            CanEditDraftId = false;
            DraftDisplayName = manifest.DisplayName ?? string.Empty;
            DraftId = manifest.Id ?? string.Empty;
            DraftFields.Clear();
            DraftRules.Clear();
            DraftExcludedDirectoryNameRules.Clear();
            DraftTextFileFields.Clear();
            GuidedPathSegments = [];
            HasAnalyzedPath = false;
            IsAssignmentPickerOpen = false;
            _activeAssignmentField = null;
            _analyzedNormalizedPath = string.Empty;
            foreach (var field in (manifest.Fields ?? [])
                         .OrderBy(static field => field.Order))
            {
                AddFieldRow(new ProfileFieldDraftViewModel(field));
            }

            foreach (var rule in manifest.Rules ?? [])
            {
                AddRuleRow(new ProfileRuleDraftViewModel(rule));
            }

            foreach (var rule in manifest.ExcludedDirectoryNameRules ?? [])
            {
                AddDirectoryExclusionRuleRow(
                    new ProfileDirectoryNameExclusionRuleDraftViewModel(rule));
            }

            foreach (var field in manifest.TextFileFields ?? [])
            {
                AddTextFileFieldRow(new ProfileTextFileFieldDraftViewModel(field));
            }

            AddDirectoryExclusionRuleCommand.NotifyCanExecuteChanged();
            AddTextFileFieldCommand.NotifyCanExecuteChanged();

            RestoreTerminalField(manifest.Rules?.FirstOrDefault()?.StopTraversalWhenCapturedGroups);

            if (TryResolveGuidedTemplate(out var template))
            {
                DraftPathTemplate = template;
                EditorMode = ProfileEditorMode.Guided;
                CanUseGuidedMode = true;
            }
            else
            {
                DraftPathTemplate = manifest.Rules?.FirstOrDefault()?.PathTemplate ?? string.Empty;
                EditorMode = ProfileEditorMode.Expert;
                CanUseGuidedMode = false;
                GeneratedPatternPreview = string.Empty;
                TemplateStatusMessage = L(
                    "Loc.Profiles.Status.OpenedExpert",
                    "이 프로필은 고급 정규식 기능을 사용하므로 전문가 모드로 열었습니다.");
                TemplateStatusSeverity = InfoBarSeverity.Informational;
            }

            SamplePath = string.Empty;
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        if (IsGuidedMode)
        {
            RefreshTemplatePreview();
        }

        IsDraftDirty = false;
        OpenSavedProfileInPlaygroundCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenSavedProfileInPlayground));
    }

    private void ApplyReview(ProfileDefinitionReview review, string successMessage)
    {
        var errors = review.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == ProfileDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length == 0 && review.IsValid)
        {
            SetEditorStatus(successMessage, InfoBarSeverity.Success);
            return;
        }

        SetEditorStatus(
            string.Join(
                Environment.NewLine,
                errors.Take(6).Select(diagnostic =>
                    $"• {ProfileDiagnosticLocalizer.Translate(_localizer, diagnostic)}"))
            + (errors.Length > 6
                ? Environment.NewLine + F(
                    "Loc.Profiles.Validation.MoreErrors",
                    "• 그 외 {0:N0}개 오류",
                    errors.Length - 6)
                : string.Empty),
            InfoBarSeverity.Error);
    }

    private async Task RefreshEditableProfilesAsync(string? preferredProfileId = null)
    {
        try
        {
            var profiles = await _authoringService.ListAsync().ConfigureAwait(true);
            EditableProfiles = profiles;
            var preferredId = preferredProfileId ?? SelectedEditableProfile?.Id;
            SelectedEditableProfile = profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not enumerate editable profiles.");
            SetEditorStatus(
                L(
                    "Loc.Profiles.Error.ListFailed",
                    "편집 가능한 프로필 목록을 불러오지 못했습니다."),
                InfoBarSeverity.Error);
        }
    }

    private async Task RunEditorOperationAsync(Func<Task> operation)
    {
        if (IsEditorBusy)
        {
            return;
        }

        IsEditorBusy = true;
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Profile editor operation failed.");
            SetEditorStatus(
                L(
                    "Loc.Profiles.Error.OperationFailed",
                    "프로필 작업을 완료하지 못했습니다. 입력값을 확인하고 다시 시도하세요."),
                InfoBarSeverity.Error);
        }
        finally
        {
            IsEditorBusy = false;
        }
    }

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySnapshot(eventArgs.Current, SelectedProfile?.Id);
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(() =>
                ApplySnapshot(eventArgs.Current, SelectedProfile?.Id));
        }
    }

    private void ApplySnapshot(ProfileCatalogSnapshot snapshot, string? preferredProfileId)
    {
        Profiles = snapshot.Profiles
            .Select(profile => new ProfileSummaryViewModel(profile.Descriptor, _localizer))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Reports = snapshot.Reports
            .Select(report => new PluginReportViewModel(report, _localizer))
            .OrderBy(static report => report.Status)
            .ThenBy(static report => report.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
        OpenSavedProfileInPlaygroundCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenSavedProfileInPlayground));
    }

    private void AddFieldRow(ProfileFieldDraftViewModel field)
    {
        field.PropertyChanged += OnDraftRowChanged;
        DraftFields.Add(field);
        MarkDraftChanged();
    }

    private void AddRuleRow(ProfileRuleDraftViewModel rule)
    {
        rule.PropertyChanged += OnDraftRowChanged;
        DraftRules.Add(rule);
        MarkDraftChanged();
    }

    private void AddDirectoryExclusionRuleRow(
        ProfileDirectoryNameExclusionRuleDraftViewModel rule)
    {
        rule.PropertyChanged += OnDraftRowChanged;
        DraftExcludedDirectoryNameRules.Add(rule);
        MarkDraftChanged();
    }

    private void AddTextFileFieldRow(ProfileTextFileFieldDraftViewModel field)
    {
        field.PropertyChanged += OnDraftRowChanged;
        DraftTextFileFields.Add(field);
        MarkDraftChanged();
    }

    private void DetachRows()
    {
        foreach (var field in DraftFields)
        {
            field.PropertyChanged -= OnDraftRowChanged;
        }

        foreach (var rule in DraftRules)
        {
            rule.PropertyChanged -= OnDraftRowChanged;
        }

        foreach (var rule in DraftExcludedDirectoryNameRules)
        {
            rule.PropertyChanged -= OnDraftRowChanged;
        }


        foreach (var field in DraftTextFileFields)
        {
            field.PropertyChanged -= OnDraftRowChanged;
        }
    }

    private void OnDraftRowChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_isPopulatingDraft)
        {
            return;
        }

        if (sender is ProfileDirectoryNameExclusionRuleDraftViewModel
            or ProfileTextFileFieldDraftViewModel)
        {
            MarkDraftChanged();
            return;
        }

        if (sender is ProfileFieldDraftViewModel changedField
            && ReferenceEquals(changedField, _activeAssignmentField)
            && eventArgs.PropertyName is nameof(ProfileFieldDraftViewModel.Kind)
                or nameof(ProfileFieldDraftViewModel.DateSourcePreset))
        {
            CloseAssignmentPickerFor(changedField);
        }

        if (sender is ProfileFieldDraftViewModel field
            && eventArgs.PropertyName == nameof(ProfileFieldDraftViewModel.Kind)
            && IsGuidedMode)
        {
            _isPopulatingDraft = true;
            try
            {
                if (field.IsDateTime)
                {
                    field.EnsureDateDefaults();
                }
                else
                {
                    field.DateSourcePreset = GuidedDateSourcePreset.SingleValue;
                }
            }
            finally
            {
                _isPopulatingDraft = false;
            }
        }

        if (IsGuidedMode)
        {
            if (HasAnalyzedPath)
            {
                RebuildGuidedTemplate();
            }
            else
            {
                RefreshTemplatePreview();
            }
        }
        else
        {
            CanUseGuidedMode = TryResolveGuidedTemplate(out _);
        }

        MarkDraftChanged();
    }

    private void MarkDraftChanged()
    {
        if (_isPopulatingDraft)
        {
            return;
        }

        if (IsExpertMode)
        {
            CanUseGuidedMode = TryResolveGuidedTemplate(out _);
        }

        IsDraftDirty = true;
        SetEditorStatus(
            L(
                "Loc.Profiles.Status.Dirty",
                "변경 사항이 있습니다. 검증 후 저장하세요."),
            InfoBarSeverity.Informational);
    }

    private List<ProfileFieldManifest> BuildFieldManifests() =>
        DraftFields.Select(BuildFieldManifest).ToList();

    internal static ProfileFieldManifest BuildFieldManifest(
        ProfileFieldDraftViewModel field,
        int index)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var groupNames = ProfileFieldDraftViewModel.ParseGroupNames(field.GroupNamesText);
        return new ProfileFieldManifest
        {
            FieldId = field.FieldId.Trim(),
            GroupName = NormalizeOptional(field.GroupName),
            GroupNames = groupNames.Count == 0 ? null : groupNames,
            Header = field.Header.Trim(),
            Order = (index + 1) * 10,
            Required = field.Required,
            Kind = field.Kind,
            ParseFormat = NormalizeOptional(field.ParseFormat),
            DisplayFormat = NormalizeOptional(field.DisplayFormat),
            ValueMappings = field.ValueMappings.Count == 0
                ? null
                : field.ValueMappings.Select(static mapping =>
                    new ProfileValueMappingManifest
                    {
                        Source = mapping.Source,
                        Display = mapping.Display,
                    }).ToList(),
        };
    }

    private bool TryCompileTemplate(out ProfilePathTemplateCompileResult result)
    {
        result = _pathTemplateCompiler.Compile(DraftPathTemplate, BuildFieldManifests());
        return result.IsValid && !string.IsNullOrEmpty(result.Pattern);
    }

    private void RefreshTemplatePreview()
    {
        if (!TryCompileTemplate(out var result))
        {
            GeneratedPatternPreview = string.Empty;
            ApplyTemplateErrors(result);
            return;
        }

        GeneratedPatternPreview = result.Pattern!;
        TemplateStatusMessage = L(
            "Loc.Profiles.Status.TemplateReady",
            "경로 규칙이 준비되었습니다. 아래에서 같은 경로를 시험해 보세요.");
        TemplateStatusSeverity = InfoBarSeverity.Success;
    }

    private void ApplyTemplateErrors(ProfilePathTemplateCompileResult result)
    {
        TemplateStatusMessage = BuildTemplateErrorMessage(result);
        TemplateStatusSeverity = InfoBarSeverity.Warning;
    }

    private string BuildTemplateErrorMessage(ProfilePathTemplateCompileResult result)
    {
        if (result.Diagnostics.Count == 0)
        {
            return L("Loc.Profiles.Status.CompleteRule", "경로 규칙을 완성하세요.");
        }

        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Take(4).Select(diagnostic =>
                $"• {ProfileDiagnosticLocalizer.Translate(_localizer, diagnostic)}"));
    }

    private bool TryResolveGuidedTemplate(out string template)
    {
        template = string.Empty;
        if (DraftRules.Count != 1)
        {
            return false;
        }

        var rule = DraftRules[0];
        if (string.IsNullOrWhiteSpace(rule.PathTemplate) ||
            rule.MatchMode != ProfileRegexMatchMode.Full ||
            !rule.IgnoreCase ||
            rule.TimeoutMilliseconds != 100 ||
            DraftFields.Any(static field => !field.SupportsGuidedAssignments) ||
            !TryResolveGuidedTerminalField(rule, out _))
        {
            return false;
        }

        var result = _pathTemplateCompiler.Compile(rule.PathTemplate, BuildFieldManifests());
        if (!result.IsValid ||
            !string.Equals(result.Pattern, rule.Pattern, StringComparison.Ordinal))
        {
            return false;
        }

        template = rule.PathTemplate;
        return true;
    }

    private bool TryResolveGuidedTerminalField(
        ProfileRuleDraftViewModel rule,
        out ProfileFieldDraftViewModel? terminalField)
    {
        terminalField = null;
        var groups = ProfileFieldDraftViewModel.ParseGroupNames(rule.StopTraversalGroupsText);
        if (groups.Count == 0)
        {
            return true;
        }

        var matches = DraftFields.Where(field =>
                field.EffectiveGroupNames.SequenceEqual(groups, StringComparer.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        terminalField = matches[0];
        return true;
    }

    private string? GetRoundTripTemplate(
        ProfileRuleDraftViewModel rule,
        IReadOnlyList<ProfileFieldManifest> fields)
    {
        if (DraftRules.Count != 1 || string.IsNullOrWhiteSpace(rule.PathTemplate))
        {
            return null;
        }

        var result = _pathTemplateCompiler.Compile(rule.PathTemplate, fields);
        return result.IsValid &&
               rule.MatchMode == ProfileRegexMatchMode.Full &&
               rule.IgnoreCase &&
               rule.TimeoutMilliseconds == 100 &&
               string.Equals(result.Pattern, rule.Pattern, StringComparison.Ordinal)
            ? rule.PathTemplate
            : null;
    }

    private void ReplaceRulesWithGuidedRule(string pattern, string pathTemplate)
    {
        foreach (var rule in DraftRules)
        {
            rule.PropertyChanged -= OnDraftRowChanged;
        }

        DraftRules.Clear();
        AddRuleRow(new ProfileRuleDraftViewModel
        {
            Id = "default",
            Pattern = pattern,
            MatchMode = ProfileRegexMatchMode.Full,
            IgnoreCase = true,
            TimeoutMilliseconds = 100,
            PathTemplate = pathTemplate,
            StopTraversalGroupsText = string.Join(", ", GuidedTerminalGroups() ?? []),
        });
    }

    private void BeginAssignment(
        ProfileFieldDraftViewModel? field,
        GuidedSourcePart part)
    {
        if (field is null)
        {
            return;
        }

        if (!HasAnalyzedPath)
        {
            TemplateStatusMessage = L(
                "Loc.Profiles.Status.AnalyzeFirst",
                "먼저 실제 경로를 붙여 넣고 ‘경로 분석’을 누르세요.");
            TemplateStatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (field.IsDateTime)
        {
            field.EnsureDateDefaults();
        }

        _activeAssignmentField = field;
        _activeAssignmentPart = part;
        IsAssignmentPickerOpen = true;
        AssignmentPickerTitle = F(
            "Loc.Profiles.Status.SelectPart",
            "‘{0}’의 {1}으로 사용할 조각을 선택하세요.",
            field.Header,
            LocalizeSourcePart(part));
        TemplateStatusMessage = L(
            "Loc.Profiles.Status.ClickSegment",
            "폴더명 전체 또는 아래의 세부 조각 버튼을 클릭하세요.");
        TemplateStatusSeverity = InfoBarSeverity.Informational;
        AssignmentPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseAssignmentPickerFor(ProfileFieldDraftViewModel field)
    {
        if (!ReferenceEquals(field, _activeAssignmentField))
        {
            return;
        }

        _activeAssignmentField = null;
        IsAssignmentPickerOpen = false;
    }

    private static GuidedSourcePart? NextMissingSourcePart(ProfileFieldDraftViewModel field)
    {
        if (!field.IsDateTime || field.DateSourcePreset == GuidedDateSourcePreset.SingleValue)
        {
            return null;
        }

        var requiredParts = field.DateSourcePreset == GuidedDateSourcePreset.YearAndMonthDay
            ? new[] { GuidedSourcePart.Year, GuidedSourcePart.MonthDay }
            : new[] { GuidedSourcePart.Year, GuidedSourcePart.Month, GuidedSourcePart.Day };
        foreach (var part in requiredParts)
        {
            if (!field.HasAssignment(part))
            {
                return part;
            }
        }

        return null;
    }

    private void AnalyzeSamplePathCore(string samplePath)
    {
        _analyzedNormalizedPath = samplePath.Trim().Replace('\\', '/');
        var segments = new List<GuidedPathSegmentViewModel>();
        var segmentMatches = Regex.Matches(
            _analyzedNormalizedPath,
            @"[^/]+",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        for (var segmentIndex = 0; segmentIndex < segmentMatches.Count; segmentIndex++)
        {
            var match = segmentMatches[segmentIndex];
            var value = match.Value;
            var wholeChoice = new GuidedPathChoiceViewModel(
                segmentIndex,
                match.Index,
                match.Length,
                value,
                value,
                F("Loc.Profiles.Path.WholeFolder", "‘{0}’ 폴더 전체", value),
                IsWholeSegment: true);
            var partMatches = Regex.Matches(
                value,
                @"[^_\-\s]+",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            var partChoices = partMatches.Count > 1
                ? partMatches
                    .Cast<Match>()
                    .Select(part => new GuidedPathChoiceViewModel(
                        segmentIndex,
                        match.Index + part.Index,
                        part.Length,
                        part.Value,
                        part.Value,
                        F(
                            "Loc.Profiles.Path.Part",
                            "‘{0}’의 ‘{1}’ 부분",
                            value,
                            part.Value),
                        IsWholeSegment: false))
                    .ToArray()
                : [];
            segments.Add(new GuidedPathSegmentViewModel(
                segmentIndex,
                match.Index > 0 ? "\\" : string.Empty,
                value,
                wholeChoice,
                partChoices));
        }

        GuidedPathSegments = segments;
        HasAnalyzedPath = segments.Count > 0;
    }

    private void RebuildGuidedTemplate()
    {
        if (!HasAnalyzedPath)
        {
            return;
        }

        var assignments = DraftFields
            .SelectMany(field => field.GuidedAssignments.Select(assignment => (Field: field, Assignment: assignment)))
            .OrderBy(static item => item.Assignment.Choice.Start)
            .ToArray();
        var template = new StringBuilder();
        var cursor = 0;
        foreach (var item in assignments)
        {
            var choice = item.Assignment.Choice;
            if (choice.Start < cursor || choice.Start + choice.Length > _analyzedNormalizedPath.Length)
            {
                continue;
            }

            template.Append(EscapeTemplateLiteral(_analyzedNormalizedPath[cursor..choice.Start]));
            template.Append('{')
                .Append(item.Field.FieldId.Trim())
                .Append('@')
                .Append(item.Assignment.GroupName);
            if (!item.Field.Required)
            {
                template.Append('?');
            }

            template.Append('}');
            cursor = choice.Start + choice.Length;
        }

        template.Append(EscapeTemplateLiteral(_analyzedNormalizedPath[cursor..]));
        var wasPopulating = _isPopulatingDraft;
        _isPopulatingDraft = true;
        try
        {
            DraftPathTemplate = template.ToString();
        }
        finally
        {
            _isPopulatingDraft = wasPopulating;
        }

        RefreshTemplatePreview();
    }

    private static string EscapeTemplateLiteral(string value) =>
        value
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);

    private List<string>? GuidedTerminalGroups()
    {
        var field = DraftFields.FirstOrDefault(static candidate => candidate.IsTerminalField);
        return field?.EffectiveGroupNames.Count > 0
            ? field.EffectiveGroupNames.ToList()
            : null;
    }

    private void RestoreTerminalField(IReadOnlyList<string>? terminalGroups)
    {
        if (terminalGroups is null || terminalGroups.Count == 0)
        {
            return;
        }

        var field = DraftFields.FirstOrDefault(candidate =>
            candidate.EffectiveGroupNames.SequenceEqual(terminalGroups, StringComparer.Ordinal));
        if (field is not null)
        {
            field.IsTerminalField = true;
        }
    }

    private static List<string>? ParseNameList(string? value)
    {
        var groupNames = ProfileFieldDraftViewModel.ParseGroupNames(value);
        return groupNames.Count == 0 ? null : groupNames;
    }

    private int NextFieldNumber()
    {
        var number = 1;
        while (DraftFields.Any(field =>
                   string.Equals(field.FieldId, $"field-{number}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(field.GroupName, $"field{number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        return number;
    }

    private void SetEditorStatus(string message, InfoBarSeverity severity)
    {
        EditorStatusMessage = message;
        EditorStatusSeverity = severity;
    }

    private string LocalizeSourcePart(GuidedSourcePart part) => part switch
    {
        GuidedSourcePart.Year => L("Loc.Profiles.SourcePart.Year", "연도"),
        GuidedSourcePart.MonthDay => L("Loc.Profiles.SourcePart.MonthDay", "월일"),
        GuidedSourcePart.Month => L("Loc.Profiles.SourcePart.Month", "월"),
        GuidedSourcePart.Day => L("Loc.Profiles.SourcePart.Day", "일"),
        _ => L("Loc.Profiles.SourcePart.Value", "값"),
    };

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer.Format(key, koreanFallback, arguments);

    private static bool Move<T>(ObservableCollection<T> items, T? item, int offset)
        where T : class
    {
        if (item is null)
        {
            return false;
        }

        var oldIndex = items.IndexOf(item);
        var newIndex = oldIndex + offset;
        if (oldIndex >= 0 && newIndex >= 0 && newIndex < items.Count)
        {
            items.Move(oldIndex, newIndex);
            return true;
        }

        return false;
    }

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProfileSummaryViewModel(
    ProfileDescriptor descriptor,
    IAppLocalizer? localizer = null)
{
    public ProfileDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Version => Descriptor.Version;

    public string Kind => Descriptor.Kind == ProfileKind.Declarative
        ? Get("Loc.Profiles.Summary.Gui", "GUI 정의")
        : Get("Loc.Profiles.Summary.Plugin", "DLL 플러그인");

    public string CandidateKind => Descriptor.CandidateKind == ProfileCandidateKind.Directory
        ? Get("Loc.Files.Kind.Folder", "폴더")
        : Descriptor.CandidateKind.ToString();

    public int FieldCount => Descriptor.Fields.Count;

    public int ExcludedDirectoryRuleCount => Descriptor.ExcludedDirectoryNameRules.Count;

    public int TextFileFieldCount => Descriptor.TextFileFields.Count;

    private string Get(string key, string koreanFallback) =>
        localizer?.Get(key, koreanFallback) ?? koreanFallback;
}

public sealed class ProfileFieldInspectionViewModel(
    ProfileFieldDescriptor descriptor,
    IAppLocalizer? localizer = null)
{
    public int Order => descriptor.Order;

    public string Header => descriptor.Header;

    public string FieldId => descriptor.FieldId;

    public string CaptureGroups => descriptor.SourceKind == ProfileFieldSourceKind.TextFileContent
        ? Get("Loc.Profiles.Field.TextFileContent", "텍스트 파일 내용")
        : string.Join(" → ", descriptor.EffectiveGroupNames);

    public string Source => descriptor.SourceKind == ProfileFieldSourceKind.TextFileContent
        ? Get("Loc.Profile.Source.TextFile", "텍스트 파일")
        : Get("Loc.Profiles.Field.PathRegex", "경로 정규식");

    public ProfileFieldValueKind Kind => descriptor.Kind;

    public bool Required => descriptor.Required;

    public bool IsNullable => descriptor.IsNullable;

    public string? ParseFormat => descriptor.ParseFormat;

    public string? DisplayFormat => descriptor.DisplayFormat;

    public string ValueMappings => descriptor.ValueMappings.Count == 0
        ? "—"
        : localizer?.Format(
            "Loc.Profiles.Field.MappingCount",
            "{0:N0}개",
            descriptor.ValueMappings.Count)
          ?? $"{descriptor.ValueMappings.Count:N0}개";

    private string Get(string key, string koreanFallback) =>
        localizer?.Get(key, koreanFallback) ?? koreanFallback;
}

public sealed class ProfileTextFileFieldInspectionViewModel(
    ProfileTextFileFieldDescriptor descriptor)
{
    public int Order => descriptor.Order;

    public string Header => descriptor.Header;

    public string FieldId => descriptor.FieldId;

    public string FileNamePattern => descriptor.FileNamePattern;

    public ProfileRegexMatchMode MatchMode => descriptor.MatchMode;

    public bool IgnoreCase => descriptor.IgnoreCase;

    public bool Required => descriptor.Required;

    public int TimeoutMilliseconds => descriptor.TimeoutMilliseconds;

    public long MaxBytes => descriptor.MaxBytes;
}

public sealed class ProfileRuleInspectionViewModel(
    ProfileRegexRuleDescriptor descriptor,
    IAppLocalizer? localizer = null)
{
    public int Order => descriptor.Order;

    public string Id => descriptor.Id;

    public ProfileRegexMatchMode MatchMode => descriptor.MatchMode;

    public bool IgnoreCase => descriptor.IgnoreCase;

    public int TimeoutMilliseconds => descriptor.TimeoutMilliseconds;

    public string Pattern => descriptor.Pattern;

    public string StopTraversalGroups => descriptor.StopTraversalWhenCapturedGroups.Count == 0
        ? localizer?.Get(
            "Loc.Profiles.Rule.NoTraversalStop",
            "없음 · 계속 탐색") ?? "없음 · 계속 탐색"
        : string.Join(" + ", descriptor.StopTraversalWhenCapturedGroups);
}

public sealed class ProfileDirectoryNameExclusionRuleInspectionViewModel(
    ProfileDirectoryNameExclusionRuleDescriptor descriptor)
{
    public int Order => descriptor.Order;

    public string Id => descriptor.Id;

    public ProfileRegexMatchMode MatchMode => descriptor.MatchMode;

    public bool IgnoreCase => descriptor.IgnoreCase;

    public int TimeoutMilliseconds => descriptor.TimeoutMilliseconds;

    public string Pattern => descriptor.Pattern;
}

public sealed class PluginReportViewModel(
    ProfilePluginReport report,
    IAppLocalizer? localizer = null)
{
    public string SourceDirectory { get; } = report.SourceDirectory;

    public string ProfileId { get; } = report.ProfileId ?? "—";

    public string DisplayName { get; } = report.DisplayName
        ?? localizer?.Get("Loc.Profiles.Report.Unknown", "알 수 없는 프로필")
        ?? "알 수 없는 프로필";

    public ProfilePluginStatus Status { get; } = report.Status;

    public string StatusText => Status == ProfilePluginStatus.Loaded
        ? localizer?.Get("Loc.Profiles.Report.Available", "사용 가능") ?? "사용 가능"
        : localizer?.Get("Loc.Profiles.Report.Unavailable", "사용 불가") ?? "사용 불가";

    public string Diagnostics { get; } = report.Diagnostics.Count == 0
        ? localizer?.Get("Loc.Profiles.Report.NoIssues", "문제 없음") ?? "문제 없음"
        : string.Join(
            Environment.NewLine,
            report.Diagnostics.Select(diagnostic =>
                $"[{diagnostic.Severity}] {diagnostic.Code}: "
                + ProfileDiagnosticLocalizer.Translate(localizer, diagnostic)
                + (string.IsNullOrWhiteSpace(diagnostic.Detail)
                    ? string.Empty
                    : $" ({diagnostic.Detail})")));
}
