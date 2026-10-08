using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Filtering;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel : ObservableObject
{
    private readonly ICatalogService _catalogService;
    private readonly IProfileCatalog _profileCatalog;
    private readonly IWorkspaceContext _workspaceContext;
    private readonly IDesktopPickerService _pickerService;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly IScanConsolePanelController _scanConsolePanelController;
    private readonly IScanCompletionNotifier _scanCompletionNotifier;
    private readonly ISnackbarService _snackbarService;
    private readonly ILogger<CatalogViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private List<CatalogItemViewModel> _loadedItems = [];
    private CatalogResultSession? _activeResultSession;
    private ProfileCatalogSnapshot? _pendingProfileSnapshot;
    private long _resultSessionVersion;

    [ObservableProperty]
    private IReadOnlyList<ProfileChoiceViewModel> _profiles = [];

    [ObservableProperty]
    private ProfileChoiceViewModel? _selectedProfile;

    [ObservableProperty]
    private IReadOnlyList<ProfileFieldDescriptor> _fields = [];

    [ObservableProperty]
    private string? _rootPath;

    [ObservableProperty]
    private string? _databasePath;

    [ObservableProperty]
    private ListCollectionView _items = null!;

    [ObservableProperty]
    private CatalogItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _hasLoadedItems;

    [ObservableProperty]
    private string _filterSummary = "0개 항목";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditWorkspace))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _progressMessage = "프로필과 검색 위치를 선택하세요.";

    [ObservableProperty]
    private string _statusTitle = "준비";

    [ObservableProperty]
    private string _statusMessage = "프로필로 폴더를 바로 찾거나 기존 인덱스에서 불러올 수 있습니다.";

    [ObservableProperty]
    private bool _isStatusOpen = true;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public bool CanEditWorkspace => !IsBusy;

    public CatalogViewModel(
        ICatalogService catalogService,
        IProfileCatalog profileCatalog,
        IWorkspaceContext workspaceContext,
        IDesktopPickerService pickerService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        IScanConsolePanelController scanConsolePanelController,
        IScanCompletionNotifier scanCompletionNotifier,
        ISnackbarService snackbarService,
        ILogger<CatalogViewModel> logger)
    {
        _catalogService = catalogService;
        _profileCatalog = profileCatalog;
        _workspaceContext = workspaceContext;
        _pickerService = pickerService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _scanConsolePanelController = scanConsolePanelController;
        _scanCompletionNotifier = scanCompletionNotifier;
        _snackbarService = snackbarService;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        Items = CreateItemsView(_loadedItems);

        var workspace = workspaceContext.Current;
        RootPath = workspace.RootPath;
        DatabasePath = workspace.DatabasePath;
        ApplyProfileSnapshot(profileCatalog.Current, workspace.SelectedProfileId);
        _profileCatalog.Changed += OnProfileCatalogChanged;
        _workspaceContext.Changed += OnWorkspaceChanged;

        if (Profiles.Count == 0)
        {
            SetStatus(
                "프로필 없음",
                "로드된 프로필이 없습니다. 프로필 화면에서 진단을 확인하세요.",
                InfoBarSeverity.Warning);
        }
    }

    partial void OnSelectedProfileChanged(ProfileChoiceViewModel? value)
    {
        EndResultSession(_activeResultSession);
        Fields = value?.Profile.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .ToArray() ?? [];
        _loadedItems = [];
        Items = CreateItemsView(_loadedItems);
        SelectedItem = null;
        FilterText = string.Empty;
        HasLoadedItems = false;
        FilterSummary = "0개 항목";
        ScanCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemChanged(CatalogItemViewModel? value) =>
        OpenSelectedCommand.NotifyCanExecuteChanged();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            if (IsBusy)
            {
                _pendingProfileSnapshot = eventArgs.Current;
                return;
            }

            ApplyChangedProfileSnapshot(eventArgs.Current);
        }

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

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs eventArgs)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            RootPath = eventArgs.Current.RootPath;
            DatabasePath = eventArgs.Current.DatabasePath;
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(Apply);
        }
    }

    private void ApplyChangedProfileSnapshot(ProfileCatalogSnapshot snapshot)
    {
        var hadProfiles = Profiles.Count > 0;
        ApplyProfileSnapshot(snapshot, SelectedProfile?.Id);
        if (!hadProfiles && Profiles.Count > 0 && StatusTitle == "프로필 없음")
        {
            SetStatus(
                "준비",
                "새 프로필이 적용되었습니다. 검색 위치를 선택하세요.",
                InfoBarSeverity.Informational);
        }
        else if (hadProfiles && Profiles.Count == 0)
        {
            SetStatus(
                "프로필 없음",
                "로드된 프로필이 없습니다. 프로필 화면에서 진단을 확인하세요.",
                InfoBarSeverity.Warning);
        }
    }

    private void ApplyProfileSnapshot(ProfileCatalogSnapshot snapshot, string? preferredProfileId)
    {
        var existingChoices = Profiles.ToDictionary(
            static profile => profile.Id,
            StringComparer.OrdinalIgnoreCase);
        Profiles = snapshot.Profiles
            .Select(profile =>
                existingChoices.TryGetValue(profile.Descriptor.Id, out var existing)
                && ReferenceEquals(existing.Profile, profile)
                    ? existing
                    : new ProfileChoiceViewModel(profile))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var selected = _pickerService.PickRootDirectory(RootPath);
        if (selected is not null)
        {
            RootPath = selected;
        }
    }

    [RelayCommand]
    private void BrowseDatabase()
    {
        var selected = _pickerService.PickDatabasePath(DatabasePath);
        if (selected is not null)
        {
            DatabasePath = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ScanAsync() => RunOperationAsync("프로필로 빠르게 불러오기", discoverDirectly: true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task LoadAsync() => RunOperationAsync("기존 인덱스에서 불러오기", discoverDirectly: false);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ProgressMessage = "작업을 취소하는 중입니다…";
        _operationCoordinator.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelected()
    {
        if (SelectedItem is not null)
        {
            OpenItem(SelectedItem);
        }
    }

    [RelayCommand]
    private void OpenItem(CatalogItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _pathLauncher.OpenDirectory(item.FullPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open folder {FolderPath}.", item.FullPath);
            ShowSnackbar("폴더 열기 실패", exception.Message, ControlAppearance.Danger);
        }
    }

    private bool CanStart() => !IsBusy && SelectedProfile is not null;

    private bool CanCancel() => IsBusy;

    private bool CanOpenSelected() => SelectedItem is not null;

    private async Task RunOperationAsync(string operationName, bool discoverDirectly)
    {
        if (IsBusy)
        {
            return;
        }

        if (_operationCoordinator.IsRunning)
        {
            SetStatus(
                "다른 작업 진행 중",
                "프로필 저장이 끝난 뒤 다시 시도하세요.",
                InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        IsScanning = discoverDirectly;
        SetStatus(operationName, $"{operationName} 작업을 시작했습니다.", InfoBarSeverity.Informational);
        CatalogOperationProgressPump? progress = null;
        CatalogResultSession? resultSession = null;
        CatalogResult? result = null;
        ScanCompletionNotice? completionNotice = null;
        try
        {
            // Capture UI-bound values before the coordinator moves the operation
            // to its background scheduler.
            var workspace = ValidateWorkspace(requiresDatabase: !discoverDirectly);
            var request = CreateRequest(workspace);
            resultSession = BeginResultSession();
            progress = CreateProgress(resultSession);
            if (discoverDirectly)
            {
                _scanConsolePanelController.Show();
            }

            await _operationCoordinator.RunAsync(async cancellationToken =>
            {
                await PersistWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
                if (!discoverDirectly)
                {
                    EnsureDatabaseDirectory(workspace.DatabasePath);
                }

                result = discoverDirectly
                    ? await _catalogService.DiscoverAsync(
                        request,
                        progress,
                        cancellationToken).ConfigureAwait(false)
                    : await _catalogService.LoadExistingAsync(
                        request,
                        progress,
                        cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);

            progress.FlushAndClose();
            completionNotice = ApplyResult(
                result ?? throw new InvalidOperationException("카탈로그 결과를 받지 못했습니다."),
                discoverDirectly,
                resultSession);
        }
        catch (OperationCanceledException)
        {
            progress?.FlushAndClose();
            var partialResultCount = resultSession?.Items.Count ?? 0;
            var partialResultMessage = CreatePartialResultMessage(partialResultCount);
            SetStatus(
                "취소됨",
                $"{operationName} 작업이 취소되었습니다.{partialResultMessage}",
                InfoBarSeverity.Warning);
            ProgressMessage = partialResultCount > 0
                ? $"취소됨 · 부분 결과 {partialResultCount:N0}개"
                : "취소됨";
            ShowSnackbar(
                "작업 취소",
                $"{operationName} 작업을 취소했습니다.{partialResultMessage}",
                ControlAppearance.Caution);
        }
        catch (Exception exception)
        {
            progress?.FlushAndClose();
            _logger.LogError(exception, "{OperationName} operation failed.", operationName);
            var partialResultCount = resultSession?.Items.Count ?? 0;
            var partialResultMessage = CreatePartialResultMessage(partialResultCount);
            SetStatus(
                "오류",
                $"{exception.Message}{partialResultMessage}",
                InfoBarSeverity.Error);
            ProgressMessage = partialResultCount > 0
                ? $"작업 실패 · 부분 결과 {partialResultCount:N0}개"
                : "작업 실패";
            ShowSnackbar(
                $"{operationName} 실패",
                $"{exception.Message}{partialResultMessage}",
                ControlAppearance.Danger);
        }
        finally
        {
            progress?.FlushAndClose();
            EndResultSession(resultSession);
            IsScanning = false;
            IsBusy = false;
            if (ApplyPendingProfileSnapshot())
            {
                completionNotice = null;
            }
        }

        if (completionNotice is not null)
        {
            ShowSnackbar(
                completionNotice.Title,
                completionNotice.Message,
                completionNotice.IsPartial
                    ? ControlAppearance.Caution
                    : ControlAppearance.Success);
            if (discoverDirectly)
            {
                _scanCompletionNotifier.Notify(completionNotice);
            }
        }
    }

    private static CatalogRequest CreateRequest(WorkspaceSelection workspace) =>
        new(workspace.ProfileId, workspace.RootPath, workspace.DatabasePath);

    private static string CreatePartialResultMessage(int partialResultCount) =>
        partialResultCount > 0
        ? $" 발견한 부분 결과 {partialResultCount:N0}개는 목록에 유지됩니다."
        : string.Empty;

    private bool ApplyPendingProfileSnapshot()
    {
        if (_pendingProfileSnapshot is not { } snapshot)
        {
            return false;
        }

        _pendingProfileSnapshot = null;
        var previousProfile = SelectedProfile?.Profile;
        ApplyChangedProfileSnapshot(snapshot);
        if (ReferenceEquals(previousProfile, SelectedProfile?.Profile))
        {
            return false;
        }

        SetStatus(
            "프로필 변경됨",
            "작업 중 프로필 구성이 변경되어 이전 규칙의 결과를 비웠습니다. 다시 불러오세요.",
            InfoBarSeverity.Warning);
        ProgressMessage = "프로필이 변경되었습니다.";
        return true;
    }

    private CatalogOperationProgressPump CreateProgress(CatalogResultSession session) =>
        new(
            _dispatcher,
            (message, matchedItems) => ApplyProgress(session, message, matchedItems),
            exception => HandleProgressDisplayFailure(session, exception));

    private CatalogResultSession BeginResultSession()
    {
        var fields = Fields;
        _loadedItems.Clear();
        Items.Refresh();
        SelectedItem = null;
        HasLoadedItems = false;
        ApplyFilter();

        var session = new CatalogResultSession(
            Interlocked.Increment(ref _resultSessionVersion),
            _loadedItems,
            fields);
        _activeResultSession = session;
        return session;
    }

    private void ApplyProgress(
        CatalogResultSession session,
        string? message,
        IReadOnlyList<CatalogItem> matchedItems)
    {
        if (!ReferenceEquals(_activeResultSession, session)
            || session.Version != Volatile.Read(ref _resultSessionVersion)
            || !ReferenceEquals(_loadedItems, session.Items))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressMessage = message;
        }

        if (matchedItems.Count == 0)
        {
            return;
        }

        // Materialize before mutating the backing list. If one item cannot be
        // formatted, the batch is rejected as a whole and the view stays in sync.
        var viewModels = matchedItems.Select(item =>
            new CatalogItemViewModel(item, session.Fields)).ToList();
        var selectedPath = SelectedItem?.FullPath;
        session.Items.AddRange(viewModels);
        HasLoadedItems = true;
        ApplyFilter(selectedPath);
    }

    private void EndResultSession(CatalogResultSession? session)
    {
        if (session is null || !ReferenceEquals(_activeResultSession, session))
        {
            return;
        }

        _activeResultSession = null;
        _ = Interlocked.Increment(ref _resultSessionVersion);
    }

    private void HandleProgressDisplayFailure(
        CatalogResultSession session,
        Exception exception)
    {
        _logger.LogWarning(exception, "Could not render live catalog results.");
        if (ReferenceEquals(_activeResultSession, session))
        {
            ProgressMessage = "실시간 표시를 일시 중단했습니다. 완료 후 전체 결과를 표시합니다.";
        }
    }

    private ScanCompletionNotice ApplyResult(
        CatalogResult result,
        bool discoveredDirectly,
        CatalogResultSession resultSession)
    {
        var selectedPath = SelectedItem?.FullPath;
        var fields = result.Profile.Fields.OrderBy(static field => field.Order).ToArray();
        if (!Fields.SequenceEqual(fields))
        {
            Fields = fields;
        }
        if (!ReferenceEquals(_activeResultSession, resultSession)
            || !ReferenceEquals(_loadedItems, resultSession.Items))
        {
            throw new InvalidOperationException("구조화 결과 세션이 작업 도중 변경되었습니다.");
        }

        // The final result remains authoritative. Reconcile the same backing list
        // instead of replacing the view so filters and user-selected sorting survive.
        var liveItemsMatchFinalResult = resultSession.Fields.SequenceEqual(fields)
            && resultSession.Items.Count == result.Items.Count
            && resultSession.Items.Zip(result.Items).All(static pair =>
                CatalogItemsAreEquivalent(pair.First, pair.Second));
        if (!liveItemsMatchFinalResult)
        {
            resultSession.Items.Clear();
            resultSession.Items.AddRange(result.Items.Select(item =>
                new CatalogItemViewModel(item, fields)));
        }

        _loadedItems = resultSession.Items;
        HasLoadedItems = _loadedItems.Count > 0;
        ApplyFilter(selectedPath);
        ProgressMessage = $"{_loadedItems.Count:N0}개 항목을 불러왔습니다.";

        var scanIncomplete = result.ScanReport is { } scanReport
            && (scanReport.Status != IndexScanStatus.Completed
                || scanReport.Errors.Count > 0);
        var discoveryIncomplete = result.DiscoveryReport is { } discoveryReport
            && (discoveryReport.Status != DirectoryDiscoveryStatus.Completed
                || discoveryReport.Progress.ErrorCount > 0);
        var exclusionIncomplete = result.DirectoryExclusionIssues.Count > 0;
        var incomplete = result.HasPendingScopes
            || scanIncomplete
            || discoveryIncomplete
            || exclusionIncomplete;
        var severity = incomplete
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success;
        var scanSummary = result.ScanReport is null
            ? string.Empty
            : $" · 인덱싱 상태 {result.ScanReport.Status} · 오류 {result.ScanReport.Errors.Count:N0}";
        var discoverySummary = result.DiscoveryReport is null
            ? string.Empty
            : $" · 방문 폴더 {result.DiscoveryReport.Progress.Directories:N0}"
              + $" · 하위 탐색 생략 {result.DiscoveryReport.Progress.PrunedDirectories:N0}"
              + $" · 오류 {result.DiscoveryReport.Progress.ErrorCount:N0}"
              + (discoveredDirectly
                  ? $" · 소요 {CatalogElapsedTimeFormatter.Format(result.DiscoveryReport.Progress.Elapsed)}"
                  : string.Empty);
        var exclusionSummary = discoveredDirectly
            ? $" · 이름 규칙 제외 {result.ExcludedDirectoryCount:N0}"
              + (exclusionIncomplete
                  ? $" · 제외 규칙 경고 {result.DirectoryExclusionIssues.Count:N0}"
                  : string.Empty)
            : string.Empty;
        var statusTitle = incomplete
            ? "부분 결과"
            : discoveredDirectly
                ? "빠른 불러오기 완료"
                : "기존 인덱스 불러오기 완료";
        var statusMessage = $"후보 {result.CandidateCount:N0} · 일치 {result.Items.Count:N0} · 규칙 외 {result.NoMatchCount:N0} · 변환 오류 {result.InvalidItems.Count:N0}{exclusionSummary}{discoverySummary}{scanSummary}"
            + (result.HasPendingScopes ? " · 아직 인덱싱되지 않은 범위가 있습니다." : string.Empty);
        SetStatus(statusTitle, statusMessage, severity);
        var completionElapsedSummary = discoveredDirectly && result.DiscoveryReport is { } report
            ? $" 소요 {CatalogElapsedTimeFormatter.Format(report.Progress.Elapsed)}"
            : string.Empty;
        return new ScanCompletionNotice(
            statusTitle,
            $"프로필 규칙에 맞는 {result.Items.Count:N0}개 폴더를 찾았습니다."
                + completionElapsedSummary
                + (incomplete ? " 일부 경로는 확인이 필요합니다." : string.Empty),
            incomplete);
    }

    private static bool CatalogItemsAreEquivalent(
        CatalogItemViewModel displayed,
        CatalogItem finalItem) =>
        string.Equals(
            displayed.FullPath,
            finalItem.FullPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            displayed.RelativePath,
            finalItem.RelativePath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            displayed.MatchedRuleId,
            finalItem.MatchedRuleId,
            StringComparison.Ordinal)
        && displayed.CoveragePending == finalItem.CoveragePending
        && displayed.Values.Count == finalItem.Values.Count
        && finalItem.Values.All(pair =>
            displayed.Values.TryGetValue(pair.Key, out var displayedValue)
            && Equals(displayedValue, pair.Value));

    private void ApplyFilter(string? preferredSelectedPath = null)
    {
        var selectedPath = preferredSelectedPath ?? SelectedItem?.FullPath;
        Items.Refresh();
        SelectedItem = selectedPath is null
            ? null
            : Items.Cast<CatalogItemViewModel>().FirstOrDefault(item =>
                string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        SelectedItem ??= Items.Count > 0
            ? (CatalogItemViewModel)Items.GetItemAt(0)
            : null;

        FilterSummary = TextFilter.Normalize(FilterText).Length == 0
            ? $"{_loadedItems.Count:N0}개 항목"
            : $"{Items.Count:N0} / {_loadedItems.Count:N0}개 항목";
    }

    private ListCollectionView CreateItemsView(List<CatalogItemViewModel> items)
    {
        var view = new ListCollectionView(items)
        {
            Filter = value => value is CatalogItemViewModel item && item.Matches(FilterText),
        };
        return view;
    }

    private WorkspaceSelection ValidateWorkspace(bool requiresDatabase)
    {
        if (SelectedProfile is null)
        {
            throw new InvalidOperationException("검색 프로필을 선택하세요.");
        }

        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException("검색할 루트 폴더를 선택하세요.");
        }

        var rootPath = Path.GetFullPath(RootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"루트 폴더를 찾을 수 없습니다: {rootPath}");
        }

        var databasePath = string.IsNullOrWhiteSpace(DatabasePath)
            ? _workspaceContext.Current.DatabasePath
            : Path.GetFullPath(DatabasePath);
        if (requiresDatabase && string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("인덱스 데이터베이스 경로를 선택하세요.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        if (requiresDatabase)
        {
            var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            if (string.Equals(databasePath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || databasePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "인덱스 데이터베이스는 검색 루트 밖에 저장해야 합니다.");
            }
        }

        return new WorkspaceSelection(SelectedProfile.Id, normalizedRoot, databasePath);
    }

    private async Task PersistWorkspaceAsync(
        WorkspaceSelection workspace,
        CancellationToken cancellationToken)
    {
        await _workspaceContext.SaveAsync(
            new WorkspaceSnapshot(
                workspace.ProfileId,
                workspace.RootPath,
                workspace.DatabasePath),
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("데이터베이스의 상위 폴더를 확인할 수 없습니다.");
        }

        Directory.CreateDirectory(directory);
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    private void ShowSnackbar(
        string title,
        string message,
        ControlAppearance appearance) =>
        _snackbarService.Show(title, message, appearance, null, TimeSpan.FromSeconds(4));

    private sealed record CatalogResultSession(
        long Version,
        List<CatalogItemViewModel> Items,
        IReadOnlyList<ProfileFieldDescriptor> Fields);

    private sealed class CatalogOperationProgressPump : IProgress<CatalogOperationProgress>
    {
        private const int DrainBatchSize = 500;
        private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(50);
        private readonly object _gate = new();
        private readonly Queue<CatalogItem> _pendingItems = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _drainTimer;
        private readonly Action<string?, IReadOnlyList<CatalogItem>> _applyProgress;
        private readonly Action<Exception> _reportFailure;
        private string? _latestMessage;
        private bool _drainScheduled;
        private bool _closed;

        public CatalogOperationProgressPump(
            Dispatcher dispatcher,
            Action<string?, IReadOnlyList<CatalogItem>> applyProgress,
            Action<Exception> reportFailure)
        {
            _dispatcher = dispatcher;
            _applyProgress = applyProgress;
            _reportFailure = reportFailure;
            _drainTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                dispatcher)
            {
                Interval = DrainInterval,
            };
            _drainTimer.Tick += OnDrainTimerTick;
        }

        public void Report(CatalogOperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var scheduleDrain = false;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _latestMessage = value.Message;
                if (value.MatchedItem is { } item)
                {
                    _pendingItems.Enqueue(item);
                }

                if (!_drainScheduled)
                {
                    _drainScheduled = true;
                    scheduleDrain = true;
                }
            }

            if (scheduleDrain)
            {
                ScheduleDrain();
            }
        }

        public void FlushAndClose()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(FlushAndClose);
                return;
            }

            List<CatalogItem> batch;
            string? message;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                batch = DrainPendingItems(int.MaxValue);
                message = _latestMessage;
                _latestMessage = null;
                _drainScheduled = false;
            }

            _drainTimer.Stop();
            _drainTimer.Tick -= OnDrainTimerTick;
            ApplySafely(message, batch);
        }

        private void ScheduleDrain()
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                CloseWithoutApplying();
                return;
            }

            try
            {
                _ = _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    StartDrainTimer);
            }
            catch (InvalidOperationException)
            {
                CloseWithoutApplying();
            }
        }

        private void StartDrainTimer()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    _drainScheduled = false;
                    return;
                }
            }

            _drainTimer.Start();
        }

        private void OnDrainTimerTick(object? sender, EventArgs eventArgs)
        {
            List<CatalogItem> batch;
            string? message;
            bool stopTimer;
            lock (_gate)
            {
                if (_closed)
                {
                    _drainScheduled = false;
                    _drainTimer.Stop();
                    return;
                }

                batch = DrainPendingItems(DrainBatchSize);
                message = _latestMessage;
                _latestMessage = null;
                stopTimer = _pendingItems.Count == 0;
                if (stopTimer)
                {
                    _drainScheduled = false;
                }
            }

            if (stopTimer)
            {
                _drainTimer.Stop();
            }

            ApplySafely(message, batch);
        }

        private void ApplySafely(string? message, IReadOnlyList<CatalogItem> batch)
        {
            try
            {
                _applyProgress(message, batch);
            }
            catch (Exception exception)
            {
                CloseWithoutApplying();
                _reportFailure(exception);
            }
        }

        private void CloseWithoutApplying()
        {
            lock (_gate)
            {
                _closed = true;
                _pendingItems.Clear();
                _latestMessage = null;
                _drainScheduled = false;
            }

            if (_dispatcher.CheckAccess())
            {
                _drainTimer.Stop();
                _drainTimer.Tick -= OnDrainTimerTick;
            }
        }

        private List<CatalogItem> DrainPendingItems(int maximumCount)
        {
            var batch = new List<CatalogItem>(Math.Min(maximumCount, _pendingItems.Count));
            while (batch.Count < maximumCount && _pendingItems.Count > 0)
            {
                batch.Add(_pendingItems.Dequeue());
            }

            return batch;
        }
    }

    private sealed record WorkspaceSelection(
        string ProfileId,
        string RootPath,
        string DatabasePath);
}
