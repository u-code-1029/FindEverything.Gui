using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class ProfileDirectoryExclusionTestResultViewModel(
    string absolutePath,
    string title,
    string message,
    InfoBarSeverity severity)
{
    public string AbsolutePath { get; } = absolutePath;

    public string Title { get; } = title;

    public string Message { get; } = message;

    public InfoBarSeverity Severity { get; } = severity;
}

public sealed class ProfilePlaygroundProfileViewModel(
    ILoadedProfile profile,
    IAppLocalizer? localizer = null)
{
    internal ILoadedProfile Profile { get; } = profile;

    public string Id => Profile.Descriptor.Id;

    public string DisplayName => Profile.Descriptor.DisplayName;

    public string Description => localizer?.Format(
        "Loc.Playground.Profile.Description",
        "{0} · {1:N0}개 컬럼",
        DisplayName,
        Profile.Descriptor.Fields.Count)
        ?? $"{DisplayName} · {Profile.Descriptor.Fields.Count:N0}개 컬럼";
}

public partial class ProfilePlaygroundViewModel : ObservableObject, IPageSession
{
    private readonly IProfileCatalog _profileCatalog;
    private readonly IProfilePlaygroundEvaluator _evaluator;
    private readonly IDesktopPickerService _pickerService;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<ProfilePlaygroundViewModel> _logger;
    private readonly IProfileEditorNavigator? _editorNavigator;
    private readonly OutputPreviewSampleStore? _outputSamples;
    private string? _preferredProfileId;
    private long _evaluationGeneration;
    private long _busyOperationVersion;
    private volatile bool _isActive;
    private volatile bool _disposed;
    private ProfileCatalogSnapshot? _pendingCatalog;
    private int _catalogRefreshScheduled;
    private CancellationTokenSource? _evaluationCancellation;

    [ObservableProperty]
    private IReadOnlyList<ProfilePlaygroundProfileViewModel> _profiles = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditProfileCommand))]
    private ProfilePlaygroundProfileViewModel? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(EditProfileCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = "프로필과 실제 절대 경로를 선택해 결과를 확인하세요.";

    [ObservableProperty]
    private InfoBarSeverity _summarySeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private IReadOnlyList<ProfileTestResultViewModel> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexDebugRuleViewModel> _regexDebugRules = [];

    [ObservableProperty]
    private string _regexDebugInputPath = string.Empty;

    [ObservableProperty]
    private ProfileDirectoryExclusionTestResultViewModel? _directoryExclusionTestResult;

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexDebugRuleViewModel>
        _directoryExclusionRegexDebugRules = [];

    public ProfilePlaygroundViewModel(
        IProfileCatalog profileCatalog,
        IProfilePlaygroundEvaluator evaluator,
        IDesktopPickerService pickerService,
        IAppLocalizer localizer,
        ILogger<ProfilePlaygroundViewModel> logger,
        IProfileEditorNavigator? editorNavigator = null,
        OutputPreviewSampleStore? outputSamples = null)
    {
        _profileCatalog = profileCatalog;
        _evaluator = evaluator;
        _pickerService = pickerService;
        _localizer = localizer;
        _logger = logger;
        _editorNavigator = editorNavigator;
        _outputSamples = outputSamples;
        Summary = L(
            "Loc.Playground.Status.Initial",
            "프로필과 실제 절대 경로를 선택해 결과를 확인하세요.");
        ApplyCatalog(profileCatalog.Current);
        _profileCatalog.Changed += OnProfileCatalogChanged;
    }

    public bool HasRegexDebugRules => RegexDebugRules.Count > 0;

    private bool CanEditProfile() => IsReady && SelectedProfile is not null && _editorNavigator is not null;

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task EditProfileAsync()
    {
        if (SelectedProfile is not { } selected || _editorNavigator is null) return;
        try
        {
            if (await _editorNavigator.NavigateAsync(selected.Id).ConfigureAwait(true)) return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not return to profile {ProfileId} for editing.", selected.Id);
        }
        Summary = L("Loc.Ux.Authoring.EditUnavailable", "편집을 열지 않았습니다. 현재 초안을 유지했거나 파일로 편집할 수 없는 프로필입니다.");
        SummarySeverity = InfoBarSeverity.Informational;
    }

    public bool IsReady => !IsBusy;

    public bool HasDirectoryExclusionTestResult => DirectoryExclusionTestResult is not null;

    public bool HasDirectoryExclusionRegexDebugRules =>
        DirectoryExclusionRegexDebugRules.Count > 0;

    partial void OnSelectedProfileChanged(ProfilePlaygroundProfileViewModel? value)
    {
        _preferredProfileId = value?.Id ?? _preferredProfileId;
        SelectedSelectionRule = SelectionRules.FirstOrDefault();
        OnPropertyChanged(nameof(SelectionRules));
        PreviewSelectionRuleCommand.NotifyCanExecuteChanged();
        InvalidateEvaluation(L(
            "Loc.Playground.Status.ProfileChanged",
            "프로필이 변경되었습니다. 같은 경로를 다시 확인하세요."));
    }

    partial void OnInputPathChanged(string value) =>
        InvalidateEvaluation(L(
            "Loc.Playground.Status.PathChanged",
            "경로가 변경되었습니다. 결과를 다시 확인하세요."));

    partial void OnRegexDebugRulesChanged(
        IReadOnlyList<ProfileRegexDebugRuleViewModel> value) =>
        OnPropertyChanged(nameof(HasRegexDebugRules));

    partial void OnDirectoryExclusionTestResultChanged(
        ProfileDirectoryExclusionTestResultViewModel? value) =>
        OnPropertyChanged(nameof(HasDirectoryExclusionTestResult));

    partial void OnDirectoryExclusionRegexDebugRulesChanged(
        IReadOnlyList<ProfileRegexDebugRuleViewModel> value) =>
        OnPropertyChanged(nameof(HasDirectoryExclusionRegexDebugRules));

    public bool SelectProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ApplyPendingCatalog();
        var profile = Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return false;
        }

        _preferredProfileId = profile.Id;
        SelectedProfile = profile;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanBrowsePath))]
    private async Task BrowsePathAsync()
    {
        var selected = await _pickerService.PickRootDirectoryAsync(InputPath).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            InputPath = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private async Task EvaluateAsync()
    {
        var selectedProfile = SelectedProfile;
        if (_disposed || IsBusy || selectedProfile is null)
        {
            return;
        }

        var inputPath = InputPath;
        using var cancellation = new CancellationTokenSource();
        _evaluationCancellation = cancellation;
        var evaluationGeneration = Interlocked.Increment(ref _evaluationGeneration);
        // Re-checking the same path can change its file/folder values. A previous
        // input-rule preview belongs to the old sample even when InputPath is unchanged.
        ClearSelectionTest();
        var busyOperation = BeginBusyOperation();
        Summary = L(
            "Loc.Playground.Status.Evaluating",
            "저장된 프로필로 절대 경로를 확인하고 있습니다.");
        SummarySeverity = InfoBarSeverity.Informational;
        try
        {
            var evaluation = await _evaluator.EvaluateAsync(selectedProfile.Profile, inputPath, cancellation.Token).ConfigureAwait(true);

            if (_disposed || cancellation.IsCancellationRequested || evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            RegexDebugInputPath = evaluation.CanonicalPath;
            RegexDebugRules = evaluation.RegexDebugRules;
            DirectoryExclusionTestResult = evaluation.DirectoryExclusionResult;
            DirectoryExclusionRegexDebugRules =
                evaluation.DirectoryExclusionRegexDebugRules;
            Rows = evaluation.Rows;
            _selectionSampleItem = evaluation.SampleItem;
            if (evaluation.SampleItem is { } sample)
                _outputSamples?.Set(selectedProfile.Id,
                    SelectionOutputItem.FromPath(evaluation.CanonicalPath, fields: sample.DisplayValues));
            Summary = evaluation.Summary;
            SummarySeverity = evaluation.Severity;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Profile playground evaluation failed.");
            if (evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            ClearResults(
                L(
                    "Loc.Playground.Status.InvalidInput",
                    "이 경로를 사용할 수 없습니다. 드라이브 또는 UNC 루트를 포함한 실제 절대 경로를 확인하세요."),
                InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Profile playground evaluation failed unexpectedly.");
            if (evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            ClearResults(
                L(
                    "Loc.Playground.Status.Failed",
                    "프로필 테스트를 완료하지 못했습니다. 상세 로그를 확인하세요."),
                InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_evaluationCancellation, cancellation)) _evaluationCancellation = null;
            CompleteBusyOperation(busyOperation);
        }
    }

    private bool CanBrowsePath() => !_disposed && IsReady;

    private bool CanCancel() => IsBusy &&
        (_evaluationCancellation is { IsCancellationRequested: false } ||
         _selectionTestCancellation is { IsCancellationRequested: false });

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Interlocked.Increment(ref _evaluationGeneration);
        _evaluationCancellation?.Cancel();
        _selectionTestCancellation?.Cancel();
        ClearResults(L("Loc.Playground.Status.Cancelling",
            "취소를 요청했습니다. 진행 중인 파일·네트워크 응답이 끝나면 새 테스트를 시작할 수 있습니다."));
        CancelCommand.NotifyCanExecuteChanged();
    }

    private long BeginBusyOperation()
    {
        var version = Interlocked.Increment(ref _busyOperationVersion);
        IsBusy = true;
        return version;
    }

    private void CompleteBusyOperation(long version)
    {
        // Keep the slot occupied until the real worker returns, including after
        // navigation/cancellation of an uninterruptible filesystem operation.
        if (version == Volatile.Read(ref _busyOperationVersion)) IsBusy = false;
    }

    private void InvalidateEvaluation(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _ = Interlocked.Increment(ref _evaluationGeneration);
        _evaluationCancellation?.Cancel();
        ClearResults(message, severity);
    }

    private bool CanEvaluate() =>
        !_disposed && !IsBusy
        && SelectedProfile is not null
        && !string.IsNullOrWhiteSpace(InputPath);

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs)
    {
        if (_disposed) return;
        Volatile.Write(ref _pendingCatalog, eventArgs.Current);
        if (!_isActive || Interlocked.Exchange(ref _catalogRefreshScheduled, 1) != 0) return;
        void Apply()
        {
            Interlocked.Exchange(ref _catalogRefreshScheduled, 0);
            if (_isActive && !_disposed) ApplyPendingCatalog();
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(Apply);
        }
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isActive = true;
        ApplyPendingCatalog();
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        _isActive = false;
        Interlocked.Increment(ref _evaluationGeneration);
        _evaluationCancellation?.Cancel();
        _selectionTestCancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        _profileCatalog.Changed -= OnProfileCatalogChanged;
    }

    private void ApplyPendingCatalog()
    {
        if (Interlocked.Exchange(ref _pendingCatalog, null) is { } snapshot) ApplyCatalog(snapshot);
    }

    private void ApplyCatalog(ProfileCatalogSnapshot snapshot)
    {
        var preferredId = _preferredProfileId ?? SelectedProfile?.Id;
        Profiles = snapshot.Profiles
            .Select(profile => new ProfilePlaygroundProfileViewModel(profile, _localizer))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    private void ClearResults(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Rows = [];
        _selectionSampleItem = null;
        ClearSelectionTest();
        RegexDebugRules = [];
        RegexDebugInputPath = string.Empty;
        DirectoryExclusionRegexDebugRules = [];
        DirectoryExclusionTestResult = null;
        Summary = message;
        SummarySeverity = severity;
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);
}
