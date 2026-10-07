using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileCatalog _profileCatalog;
    private readonly IProfileAuthoringService _authoringService;
    private readonly IProfilePathTemplateCompiler _pathTemplateCompiler;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
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
    private SettingChoice<ProfilePathInput> _selectedPathInput = null!;

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
    private IReadOnlyList<ProfileTestResultViewModel> _testRows = [];

    [ObservableProperty]
    private string _testSummary = "예제 경로를 입력하면 실제 변환 결과를 미리 볼 수 있습니다.";

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

    private ProfileFieldDraftViewModel? _activeAssignmentField;

    private GuidedSourcePart _activeAssignmentPart = GuidedSourcePart.Value;

    public ProfilesViewModel(
        IProfileCatalog profileCatalog,
        IProfileAuthoringService authoringService,
        IProfilePathTemplateCompiler pathTemplateCompiler,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        ILogger<ProfilesViewModel> logger)
    {
        _profileCatalog = profileCatalog;
        _authoringService = authoringService;
        _pathTemplateCompiler = pathTemplateCompiler;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _logger = logger;

        PathInputChoices =
        [
            new(ProfilePathInput.Relative, "검색 루트 기준 상대 경로 (권장)"),
            new(ProfilePathInput.Full, "드라이브를 포함한 전체 경로"),
        ];
        FieldKindChoices =
        [
            new(ProfileFieldValueKind.String, "텍스트"),
            new(ProfileFieldValueKind.Int32, "정수"),
            new(ProfileFieldValueKind.Decimal, "소수"),
            new(ProfileFieldValueKind.DateTime, "날짜/시간"),
            new(ProfileFieldValueKind.Boolean, "참/거짓"),
        ];
        MatchModeChoices =
        [
            new(ProfileRegexMatchMode.Full, "전체 일치 (권장)"),
            new(ProfileRegexMatchMode.Partial, "부분 일치"),
        ];
        DateSourcePresetChoices =
        [
            new(GuidedDateSourcePreset.SingleValue, "한 조각에서 날짜 읽기"),
            new(GuidedDateSourcePreset.YearAndMonthDay, "연도 + 월일"),
            new(GuidedDateSourcePreset.YearMonthAndDay, "연도 + 월 + 일"),
        ];
        SelectedPathInput = PathInputChoices[0];

        ApplySnapshot(profileCatalog.Current, preferredProfileId: null);
        _profileCatalog.Changed += OnProfileCatalogChanged;
        NewDraft();
        _ = RefreshEditableProfilesAsync();
    }

    public IReadOnlyList<SettingChoice<ProfilePathInput>> PathInputChoices { get; }

    public IReadOnlyList<SettingChoice<ProfileFieldValueKind>> FieldKindChoices { get; }

    public IReadOnlyList<SettingChoice<ProfileRegexMatchMode>> MatchModeChoices { get; }

    public IReadOnlyList<SettingChoice<GuidedDateSourcePreset>> DateSourcePresetChoices { get; }

    public bool IsEditorReady => !IsEditorBusy;

    public bool IsGuidedMode => EditorMode == ProfileEditorMode.Guided;

    public bool IsExpertMode => EditorMode == ProfileEditorMode.Expert;

    public ObservableCollection<ProfileFieldDraftViewModel> DraftFields { get; } = [];

    public ObservableCollection<ProfileRuleDraftViewModel> DraftRules { get; } = [];

    public event EventHandler? AssignmentPickerRequested;

    partial void OnSelectedProfileChanged(ProfileSummaryViewModel? value)
    {
        Fields = value?.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .Select(static field => new ProfileFieldInspectionViewModel(field))
            .ToArray() ?? [];
        Rules = value?.Descriptor.Rules
            .OrderBy(static rule => rule.Order)
            .Select(static rule => new ProfileRuleInspectionViewModel(rule))
            .ToArray() ?? [];
    }

    partial void OnDraftDisplayNameChanged(string value) => MarkDraftChanged();

    partial void OnDraftIdChanged(string value) => MarkDraftChanged();

    partial void OnSelectedPathInputChanged(SettingChoice<ProfilePathInput> value) =>
        MarkDraftChanged();

    partial void OnSamplePathChanged(string value)
    {
        TestDraftCommand.NotifyCanExecuteChanged();
        if (_isPopulatingDraft)
        {
            return;
        }

        TestRows = [];
        TestSummary = "예제 경로가 변경되었습니다. 결과를 다시 확인하세요.";
    }

    partial void OnEditorModeChanged(ProfileEditorMode value)
    {
        _activeAssignmentField = null;
        IsAssignmentPickerOpen = false;
        OnPropertyChanged(nameof(IsGuidedMode));
        OnPropertyChanged(nameof(IsExpertMode));
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
        TestDraftCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        BuildTemplateFromSampleCommand.NotifyCanExecuteChanged();
        UseExpertModeCommand.NotifyCanExecuteChanged();
        UseGuidedModeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewDraft()
    {
        _isPopulatingDraft = true;
        try
        {
            DetachRows();
            _originalProfileId = null;
            _draftVersion = "1.0.0";
            CanEditDraftId = true;
            DraftDisplayName = "새 프로필";
            DraftId = $"profile-{DateTime.Now:yyyyMMddHHmmss}";
            SelectedPathInput = PathInputChoices[0];
            EditorMode = ProfileEditorMode.Guided;
            CanUseGuidedMode = true;
            _activeAssignmentField = null;
            IsAssignmentPickerOpen = false;
            DraftFields.Clear();
            DraftRules.Clear();
            AddFieldRow(new ProfileFieldDraftViewModel
            {
                Header = "이름",
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
            SamplePath = "Example";
            AnalyzeSamplePathCore(SamplePath);
            DraftFields[0].SetGuidedAssignment(
                GuidedSourcePart.Value,
                GuidedPathSegments[0].WholeChoice);
            RebuildGuidedTemplate();
            TestRows = [];
            TestSummary = "기본 예제를 바로 시험하거나 실제 경로에 맞게 수정하세요.";
            SetEditorStatus(
                "새 프로필 초안을 만들었습니다. 예제 값을 수정한 뒤 검증하세요.",
                InfoBarSeverity.Informational);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        RefreshTemplatePreview();
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
                throw new InvalidOperationException("선택한 파일은 GUI에서 편집할 수 있는 선언형 프로필이 아닙니다.");
            }

            PopulateDraft(manifest);
            SetEditorStatus(
                $"'{DraftDisplayName}' 프로필을 열었습니다.",
                InfoBarSeverity.Informational);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BuildTemplateFromSampleAsync()
    {
        if (string.IsNullOrWhiteSpace(SamplePath))
        {
            TemplateStatusMessage = "먼저 실제 폴더 경로를 붙여 넣으세요.";
            TemplateStatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var normalizedSamplePath = SamplePath.Trim().Replace('\\', '/');
        if (DraftFields.Any(static field => field.HasGuidedAssignments)
            && !string.Equals(
                normalizedSamplePath,
                _analyzedNormalizedPath,
                StringComparison.Ordinal))
        {
            var result = await _contentDialogService.ShowAsync(
                new ContentDialog
                {
                    Title = "새 경로를 분석할까요?",
                    Content = "현재 연결된 경로 조각이 초기화됩니다. 결과 값은 그대로 유지됩니다.",
                    PrimaryButtonText = "새 경로 분석",
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Close,
                },
                CancellationToken.None).ConfigureAwait(true);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }
        }

        _isPopulatingDraft = true;
        try
        {
            foreach (var field in DraftFields)
            {
                field.ClearGuidedAssignments();
            }

            AnalyzeSamplePathCore(SamplePath);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        RebuildGuidedTemplate();
        _activeAssignmentField = null;
        IsAssignmentPickerOpen = false;
        TemplateStatusMessage =
            "결과 값 카드에서 경로 조각 선택을 누른 뒤, 아래의 폴더 또는 세부 조각 버튼을 클릭하세요.";
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
            TemplateStatusMessage = $"‘{_activeAssignmentField.Header}’ 연결을 완료했습니다.";
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
                "이 정규식은 초보자 모드로 안전하게 바꿀 수 없습니다. 전문가 모드에서 계속 편집하세요.",
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
        SetEditorStatus("초보자 모드로 전환했습니다.", InfoBarSeverity.Informational);
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
                "경로 템플릿 오류를 먼저 수정한 뒤 전문가 모드로 전환하세요.",
                InfoBarSeverity.Warning);
            return;
        }

        ReplaceRulesWithGuidedRule(result.Pattern!, DraftPathTemplate);
        EditorMode = ProfileEditorMode.Expert;
        CanUseGuidedMode = true;
        SetEditorStatus(
            "전문가 모드로 전환했습니다. 정규식을 직접 바꾸면 초보자 모드로 돌아갈 수 없습니다.",
            InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private void AddField()
    {
        var number = NextFieldNumber();
        AddFieldRow(new ProfileFieldDraftViewModel
        {
            Header = $"결과 값 {number}",
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
    private void AddRule() => AddRuleRow(new ProfileRuleDraftViewModel
    {
        Id = $"rule-{DraftRules.Count + 1}",
        MatchMode = ProfileRegexMatchMode.Full,
        IgnoreCase = true,
        TimeoutMilliseconds = 100,
        Pattern = "^$",
    });

    [RelayCommand]
    private void RemoveRule(ProfileRuleDraftViewModel? rule)
    {
        if (rule is not null && DraftRules.Remove(rule))
        {
            rule.PropertyChanged -= OnDraftRowChanged;
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
            ApplyReview(review, "검증을 통과했습니다. 저장하거나 예제 경로를 시험할 수 있습니다.");
        }
        catch (InvalidOperationException exception)
        {
            SetEditorStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestDraft))]
    private void TestDraft()
    {
        ProfileDefinitionTestResult result;
        try
        {
            result = _authoringService.Test(BuildManifest(), SamplePath.Trim());
        }
        catch (Exception exception)
        {
            SetEditorStatus(exception.Message, InfoBarSeverity.Error);
            return;
        }

        if (!result.Review.IsValid || result.Mapping is null)
        {
            ApplyReview(result.Review, string.Empty);
            TestRows = [];
            TestSummary = "프로필 정의 오류를 먼저 수정하세요.";
            return;
        }

        var mapping = result.Mapping;
        switch (mapping.Status)
        {
            case ProfileMapStatus.NoMatch:
                TestRows = [];
                TestSummary = "어떤 규칙에도 일치하지 않습니다. 경로 입력 기준과 정규식을 확인하세요.";
                SetEditorStatus(TestSummary, InfoBarSeverity.Warning);
                break;
            case ProfileMapStatus.Invalid:
                TestRows = mapping.Issues
                    .Select(issue => new ProfileTestResultViewModel(
                        issue.FieldId ?? "규칙",
                        null,
                        issue.Message))
                    .ToArray();
                TestSummary = "경로는 일치했지만 값을 변환할 수 없습니다.";
                SetEditorStatus(TestSummary, InfoBarSeverity.Error);
                break;
            case ProfileMapStatus.Success when mapping.Item is not null:
                var profile = result.Review.Profile!;
                TestRows = profile.Descriptor.Fields
                    .Select(field => new ProfileTestResultViewModel(
                        field.Header,
                        FormatValue(mapping.Item.Values[field.FieldId], field.DisplayFormat),
                        BuildTestStatus(field, mapping.Item.Values[field.FieldId])))
                    .ToArray();
                TestSummary = $"규칙 '{mapping.Item.MatchedRuleId}'에 일치했고 {TestRows.Count:N0}개 값을 변환했습니다.";
                SetEditorStatus("예제 경로 시험을 통과했습니다.", InfoBarSeverity.Success);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveDraftAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            SetEditorStatus(
                "인덱싱 또는 불러오기가 끝난 뒤 프로필을 저장하세요.",
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
                await _operationCoordinator.RunAsync(async cancellationToken =>
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
                    "인덱싱 또는 불러오기가 끝난 뒤 프로필을 저장하세요.",
                    InfoBarSeverity.Warning);
                return;
            }

            if (result is null)
            {
                throw new InvalidOperationException("프로필 저장 결과를 받지 못했습니다.");
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
                    .Select(static diagnostic => diagnostic.Message)
                    .Distinct()
                    .ToArray() ?? [];
                SetEditorStatus(
                    reloadMessages.Length == 0
                        ? "파일은 저장했지만 프로필을 적용하지 못했습니다. 로드 결과의 진단을 확인하세요."
                        : string.Join(Environment.NewLine, reloadMessages),
                    InfoBarSeverity.Error);
                return;
            }

            _originalProfileId = result.Review.Profile!.Descriptor.Id;
            CanEditDraftId = false;
            if (result.Snapshot is not null)
            {
                ApplySnapshot(result.Snapshot, _originalProfileId);
            }

            await RefreshEditableProfilesAsync(_originalProfileId).ConfigureAwait(true);
            SetEditorStatus(
                "프로필을 저장하고 카탈로그에 즉시 적용했습니다.",
                InfoBarSeverity.Success);
            _snackbarService.Show(
                "프로필 저장 완료",
                $"'{result.Review.Profile.Descriptor.DisplayName}' 프로필을 바로 사용할 수 있습니다.",
                ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(4));
        }).ConfigureAwait(true);
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

    private bool CanTestDraft() => !IsEditorBusy && !string.IsNullOrWhiteSpace(SamplePath);

    private bool CanSwitchToGuided() => !IsEditorBusy && CanUseGuidedMode;

    private ProfileManifest BuildManifest()
    {
        var fields = BuildFieldManifests();
        List<ProfileRegexRuleManifest> rules;
        if (IsGuidedMode)
        {
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
            PathInput = SelectedPathInput.Value,
            Fields = fields,
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
            _draftVersion = string.IsNullOrWhiteSpace(manifest.Version)
                ? "1.0.0"
                : manifest.Version.Trim();
            CanEditDraftId = false;
            DraftDisplayName = manifest.DisplayName ?? string.Empty;
            DraftId = manifest.Id ?? string.Empty;
            SelectedPathInput = PathInputChoices.FirstOrDefault(choice =>
                    choice.Value == manifest.PathInput)
                ?? PathInputChoices[0];
            DraftFields.Clear();
            DraftRules.Clear();
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
                TemplateStatusMessage = "이 프로필은 고급 정규식 기능을 사용하므로 전문가 모드로 열었습니다.";
                TemplateStatusSeverity = InfoBarSeverity.Informational;
            }

            SamplePath = string.Empty;
            TestRows = [];
            TestSummary = "실제 경로를 입력해 저장 전에 결과를 확인하세요.";
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        if (IsGuidedMode)
        {
            RefreshTemplatePreview();
        }
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
                errors.Take(6).Select(static diagnostic => $"• {diagnostic.Message}"))
            + (errors.Length > 6 ? $"{Environment.NewLine}• 그 외 {errors.Length - 6:N0}개 오류" : string.Empty),
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
            SetEditorStatus(exception.Message, InfoBarSeverity.Error);
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
            SetEditorStatus(exception.Message, InfoBarSeverity.Error);
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
            .Select(static profile => new ProfileSummaryViewModel(profile.Descriptor))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Reports = snapshot.Reports
            .Select(static report => new PluginReportViewModel(report))
            .OrderBy(static report => report.Status)
            .ThenBy(static report => report.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
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
    }

    private void OnDraftRowChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_isPopulatingDraft)
        {
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

        TestRows = [];
        TestSummary = "초안이 변경되었습니다. 예제 경로를 다시 시험하세요.";
        SetEditorStatus("변경 사항이 있습니다. 검증 후 저장하세요.", InfoBarSeverity.Informational);
    }

    private List<ProfileFieldManifest> BuildFieldManifests() =>
        DraftFields.Select(static (field, index) =>
        {
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
            };
        }).ToList();

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
        TemplateStatusMessage = "경로 규칙이 준비되었습니다. 아래에서 같은 경로를 시험해 보세요.";
        TemplateStatusSeverity = InfoBarSeverity.Success;
    }

    private void ApplyTemplateErrors(ProfilePathTemplateCompileResult result)
    {
        TemplateStatusMessage = BuildTemplateErrorMessage(result);
        TemplateStatusSeverity = InfoBarSeverity.Warning;
    }

    private static string BuildTemplateErrorMessage(ProfilePathTemplateCompileResult result)
    {
        if (result.Diagnostics.Count == 0)
        {
            return "경로 규칙을 완성하세요.";
        }

        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Take(4).Select(static diagnostic => $"• {diagnostic.Message}"));
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
            TemplateStatusMessage = "먼저 실제 경로를 붙여 넣고 ‘경로 분석’을 누르세요.";
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
        AssignmentPickerTitle =
            $"‘{field.Header}’의 {GuidedPathAssignmentViewModel.SourcePartDisplayName(part)}으로 사용할 조각을 선택하세요.";
        TemplateStatusMessage = "폴더명 전체 또는 아래의 세부 조각 버튼을 클릭하세요.";
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
                $"‘{value}’ 폴더 전체",
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
                        $"‘{value}’의 ‘{part.Value}’ 부분",
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

    private string BuildTestStatus(ProfileFieldDescriptor field, object? value)
    {
        var draft = DraftFields.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));
        if (draft is null || draft.GuidedAssignments.Count <= 1)
        {
            return field.Required ? "필수" : "선택";
        }

        var sourceValues = draft.EffectiveGroupNames
            .Select(groupName => draft.GuidedAssignments.FirstOrDefault(assignment =>
                string.Equals(assignment.GroupName, groupName, StringComparison.Ordinal))?.Choice.Value)
            .Where(static source => !string.IsNullOrEmpty(source))
            .ToArray();
        if (sourceValues.Length <= 1)
        {
            return field.Required ? "필수" : "선택";
        }

        return $"{string.Join(" + ", sourceValues)} → {string.Concat(sourceValues)} → {FormatValue(value, field.DisplayFormat)}";
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

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProfileSummaryViewModel(ProfileDescriptor descriptor)
{
    public ProfileDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Version => Descriptor.Version;

    public string Kind => Descriptor.Kind == ProfileKind.Declarative ? "GUI 정의" : "DLL 플러그인";

    public string CandidateKind => Descriptor.CandidateKind == ProfileCandidateKind.Directory
        ? "폴더"
        : Descriptor.CandidateKind.ToString();

    public string PathInput => Descriptor.PathInput == ProfilePathInput.Relative
        ? "상대 경로"
        : "전체 경로";

    public int FieldCount => Descriptor.Fields.Count;
}

public sealed class ProfileFieldInspectionViewModel(ProfileFieldDescriptor descriptor)
{
    public int Order => descriptor.Order;

    public string Header => descriptor.Header;

    public string FieldId => descriptor.FieldId;

    public string CaptureGroups => string.Join(" → ", descriptor.EffectiveGroupNames);

    public ProfileFieldValueKind Kind => descriptor.Kind;

    public bool Required => descriptor.Required;

    public bool IsNullable => descriptor.IsNullable;

    public string? ParseFormat => descriptor.ParseFormat;

    public string? DisplayFormat => descriptor.DisplayFormat;
}

public sealed class ProfileRuleInspectionViewModel(ProfileRegexRuleDescriptor descriptor)
{
    public int Order => descriptor.Order;

    public string Id => descriptor.Id;

    public ProfileRegexMatchMode MatchMode => descriptor.MatchMode;

    public bool IgnoreCase => descriptor.IgnoreCase;

    public int TimeoutMilliseconds => descriptor.TimeoutMilliseconds;

    public string Pattern => descriptor.Pattern;

    public string StopTraversalGroups => descriptor.StopTraversalWhenCapturedGroups.Count == 0
        ? "없음 · 계속 탐색"
        : string.Join(" + ", descriptor.StopTraversalWhenCapturedGroups);
}

public sealed class PluginReportViewModel(ProfilePluginReport report)
{
    public string SourceDirectory { get; } = report.SourceDirectory;

    public string ProfileId { get; } = report.ProfileId ?? "—";

    public string DisplayName { get; } = report.DisplayName ?? "알 수 없는 프로필";

    public ProfilePluginStatus Status { get; } = report.Status;

    public string StatusText => Status == ProfilePluginStatus.Loaded ? "사용 가능" : "사용 불가";

    public string Diagnostics { get; } = report.Diagnostics.Count == 0
        ? "문제 없음"
        : string.Join(
            Environment.NewLine,
            report.Diagnostics.Select(static diagnostic =>
                $"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}"
                + (string.IsNullOrWhiteSpace(diagnostic.Detail)
                    ? string.Empty
                    : $" ({diagnostic.Detail})")));
}
