using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
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
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly ISnackbarService _snackbarService;
    private readonly ILogger<ProfilesViewModel> _logger;
    private string? _originalProfileId;
    private string _draftVersion = "1.0.0";
    private bool _isPopulatingDraft;

    [ObservableProperty]
    private IReadOnlyList<ProfileSummaryViewModel> _profiles = [];

    [ObservableProperty]
    private ProfileSummaryViewModel? _selectedProfile;

    [ObservableProperty]
    private IReadOnlyList<ProfileFieldDescriptor> _fields = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexRuleDescriptor> _rules = [];

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
    private IReadOnlyList<ProfileTestResultViewModel> _testRows = [];

    [ObservableProperty]
    private string _testSummary = "예제 경로를 입력하면 실제 변환 결과를 미리 볼 수 있습니다.";

    [ObservableProperty]
    private string _editorStatusMessage = "기본 정보, 컬럼, 경로 규칙을 입력한 뒤 검증하세요.";

    [ObservableProperty]
    private InfoBarSeverity _editorStatusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isEditorBusy;

    public ProfilesViewModel(
        IProfileCatalog profileCatalog,
        IProfileAuthoringService authoringService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        ISnackbarService snackbarService,
        ILogger<ProfilesViewModel> logger)
    {
        _profileCatalog = profileCatalog;
        _authoringService = authoringService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _snackbarService = snackbarService;
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
        SelectedPathInput = PathInputChoices[0];

        ApplySnapshot(profileCatalog.Current, preferredProfileId: null);
        _profileCatalog.Changed += OnProfileCatalogChanged;
        NewDraft();
        _ = RefreshEditableProfilesAsync();
    }

    public IReadOnlyList<SettingChoice<ProfilePathInput>> PathInputChoices { get; }

    public IReadOnlyList<SettingChoice<ProfileFieldValueKind>> FieldKindChoices { get; }

    public IReadOnlyList<SettingChoice<ProfileRegexMatchMode>> MatchModeChoices { get; }

    public bool IsEditorReady => !IsEditorBusy;

    public ObservableCollection<ProfileFieldDraftViewModel> DraftFields { get; } = [];

    public ObservableCollection<ProfileRuleDraftViewModel> DraftRules { get; } = [];

    partial void OnSelectedProfileChanged(ProfileSummaryViewModel? value)
    {
        Fields = value?.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .ToArray() ?? [];
        Rules = value?.Descriptor.Rules
            .OrderBy(static rule => rule.Order)
            .ToArray() ?? [];
    }

    partial void OnDraftDisplayNameChanged(string value) => MarkDraftChanged();

    partial void OnDraftIdChanged(string value) => MarkDraftChanged();

    partial void OnSelectedPathInputChanged(SettingChoice<ProfilePathInput> value) =>
        MarkDraftChanged();

    partial void OnSamplePathChanged(string value) => TestDraftCommand.NotifyCanExecuteChanged();

    partial void OnIsEditorBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditorReady));
        NewDraftCommand.NotifyCanExecuteChanged();
        LoadDraftCommand.NotifyCanExecuteChanged();
        ValidateDraftCommand.NotifyCanExecuteChanged();
        TestDraftCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
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
            AddRuleRow(new ProfileRuleDraftViewModel
            {
                Id = "default",
                MatchMode = ProfileRegexMatchMode.Full,
                IgnoreCase = true,
                TimeoutMilliseconds = 100,
                Pattern = "^(?<name>.+)$",
            });
            SamplePath = "Example";
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

    [RelayCommand]
    private void AddField() => AddFieldRow(new ProfileFieldDraftViewModel
    {
        Header = "새 컬럼",
        FieldId = $"field-{DraftFields.Count + 1}",
        GroupName = $"field{DraftFields.Count + 1}",
    });

    [RelayCommand]
    private void RemoveField(ProfileFieldDraftViewModel? field)
    {
        if (field is not null && DraftFields.Remove(field))
        {
            field.PropertyChanged -= OnDraftRowChanged;
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
        var review = _authoringService.Validate(BuildManifest());
        ApplyReview(review, "검증을 통과했습니다. 저장하거나 예제 경로를 시험할 수 있습니다.");
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
                        field.Required ? "필수" : "선택"))
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

    private ProfileManifest BuildManifest() =>
        new()
        {
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = DraftId.Trim(),
            Version = _draftVersion,
            DisplayName = DraftDisplayName.Trim(),
            CandidateKind = ProfileCandidateKind.Directory,
            PathInput = SelectedPathInput.Value,
            Fields = DraftFields.Select(static (field, index) => new ProfileFieldManifest
            {
                FieldId = field.FieldId.Trim(),
                GroupName = field.GroupName.Trim(),
                Header = field.Header.Trim(),
                Order = (index + 1) * 10,
                Required = field.Required,
                Kind = field.Kind,
                ParseFormat = NormalizeOptional(field.ParseFormat),
                DisplayFormat = NormalizeOptional(field.DisplayFormat),
            }).ToList(),
            Rules = DraftRules.Select(static rule => new ProfileRegexRuleManifest
            {
                Id = rule.Id.Trim(),
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
            }).ToList(),
        };

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
            foreach (var field in (manifest.Fields ?? [])
                         .OrderBy(static field => field.Order))
            {
                AddFieldRow(new ProfileFieldDraftViewModel(field));
            }

            foreach (var rule in manifest.Rules ?? [])
            {
                AddRuleRow(new ProfileRuleDraftViewModel(rule));
            }

            SamplePath = string.Empty;
            TestRows = [];
            TestSummary = "실제 경로를 입력해 저장 전에 결과를 확인하세요.";
        }
        finally
        {
            _isPopulatingDraft = false;
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

    private void OnDraftRowChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
        MarkDraftChanged();

    private void MarkDraftChanged()
    {
        if (_isPopulatingDraft)
        {
            return;
        }

        TestRows = [];
        TestSummary = "초안이 변경되었습니다. 예제 경로를 다시 시험하세요.";
        SetEditorStatus("변경 사항이 있습니다. 검증 후 저장하세요.", InfoBarSeverity.Informational);
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
