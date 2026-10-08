using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Filtering;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogService _catalogService;
    private readonly IProfileCatalog _profileCatalog;
    private readonly IWorkspaceContext _workspaceContext;
    private readonly IIndexDatabasePathResolver _databasePathResolver;
    private readonly IDesktopPickerService _pickerService;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly IScanCompletionNotifier _scanCompletionNotifier;
    private readonly ISnackbarService _snackbarService;
    private readonly ISelectionOutputFormatStore _outputFormatStore;
    private readonly ISelectionOutputFormatter _outputFormatter;
    private readonly ICatalogSelectionExporter _selectionExporter;
    private readonly IClipboardService _clipboardService;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<CatalogViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private List<CatalogItemViewModel> _loadedItems = [];
    private CatalogResultSession? _activeResultSession;
    private ProfileCatalogSnapshot? _pendingProfileSnapshot;
    private long _resultSessionVersion;
    private string? _lastAutomaticLoadKey;
    private IReadOnlyList<CatalogItemViewModel> _selectedItems = [];
    private bool _isActive;
    private bool _isAutomaticIndexLoad;
    private bool _isApplyingWorkspaceSnapshot;
    private CancellationTokenSource? _workspaceSaveDebounce;
    private long _workspaceDraftVersion;
    private WorkspaceSaveAttempt? _latestWorkspaceDraft;
    private int _workspaceDirtyFields;
    private CatalogOperationMode _activeOperationMode;
    private bool _disposed;

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
    private IReadOnlyList<SelectionOutputFormatDefinition> _outputFormats = [];

    [ObservableProperty]
    private SelectionOutputFormatDefinition? _selectedOutputFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _hasLoadedItems;

    [ObservableProperty]
    private bool _hasValueMappings;

    [ObservableProperty]
    private bool _showOriginalValues;

    [ObservableProperty]
    private string _filterSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditWorkspace))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isLoadingExistingIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusOpen = true;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public bool CanEditWorkspace => !IsBusy;

    public string DetailsButtonText => IsDetailsOpen
        ? L("Loc.Common.Compact", "간단히")
        : L("Loc.Common.Details", "자세히");

    public bool HasSelection => SelectedCount > 0;

    public IAppLocalizer Localizer => _localizer;

    public CatalogViewModel(
        ICatalogService catalogService,
        IProfileCatalog profileCatalog,
        IWorkspaceContext workspaceContext,
        IIndexDatabasePathResolver databasePathResolver,
        IDesktopPickerService pickerService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        IScanCompletionNotifier scanCompletionNotifier,
        ISnackbarService snackbarService,
        ISelectionOutputFormatStore outputFormatStore,
        ISelectionOutputFormatter outputFormatter,
        ICatalogSelectionExporter selectionExporter,
        IClipboardService clipboardService,
        IAppLocalizer localizer,
        ILogger<CatalogViewModel> logger)
    {
        _catalogService = catalogService;
        _profileCatalog = profileCatalog;
        _workspaceContext = workspaceContext;
        _databasePathResolver = databasePathResolver;
        _pickerService = pickerService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _scanCompletionNotifier = scanCompletionNotifier;
        _snackbarService = snackbarService;
        _outputFormatStore = outputFormatStore;
        _outputFormatter = outputFormatter;
        _selectionExporter = selectionExporter;
        _clipboardService = clipboardService;
        _localizer = localizer;
        _logger = logger;
        _filterSummary = F("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", 0);
        _progressMessage = L(
            "Loc.Catalog.Progress.SelectProfileAndRoot",
            "프로필과 검색 위치를 선택하세요.");
        _statusTitle = L("Loc.Common.Ready", "준비");
        _statusMessage = L(
            "Loc.Catalog.Status.Ready.Message",
            "스캔하면 결과를 바로 보여주고 파일과 폴더 메타데이터를 DB에 저장합니다.");
        _dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        Items = CreateItemsView(_loadedItems);

        var workspace = workspaceContext.Current;
        ApplyWorkspaceSnapshot(workspace);
        ApplyProfileSnapshot(profileCatalog.Current, workspace.SelectedProfileId);
        _profileCatalog.Changed += OnProfileCatalogChanged;
        _workspaceContext.Changed += OnWorkspaceChanged;
        _outputFormatStore.Changed += OnOutputFormatsChanged;
        _operationCoordinator.StateChanged += OnOperationCoordinatorStateChanged;
        RefreshOutputFormats();

        if (Profiles.Count == 0)
        {
            SetStatus(
                L("Loc.Catalog.Status.NoProfiles", "프로필 없음"),
                L(
                    "Loc.Catalog.Status.NoProfiles.Message",
                    "로드된 프로필이 없습니다. 프로필 화면에서 진단을 확인하세요."),
                InfoBarSeverity.Warning);
        }
    }

    public void Activate()
    {
        _isActive = true;
        ScheduleAutomaticLoad();
    }

    public void Deactivate()
    {
        _isActive = false;
        if (!_isAutomaticIndexLoad
            || !IsLoadingExistingIndex
            || _operationCoordinator.CurrentKind != ApplicationOperationKind.IndexLoad)
        {
            return;
        }

        _lastAutomaticLoadKey = null;
        ProgressMessage = L(
            "Loc.Catalog.Progress.CancelAutomaticLoadOnClose",
            "페이지를 닫아 자동 불러오기를 취소하는 중…");
        _operationCoordinator.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelWorkspacePersistenceDebounce();
        _profileCatalog.Changed -= OnProfileCatalogChanged;
        _workspaceContext.Changed -= OnWorkspaceChanged;
        _outputFormatStore.Changed -= OnOutputFormatsChanged;
        _operationCoordinator.StateChanged -= OnOperationCoordinatorStateChanged;
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedProfileChanged(ProfileChoiceViewModel? value)
    {
        _lastAutomaticLoadKey = null;
        EndResultSession(_activeResultSession);
        Fields = value?.Profile.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .ToArray() ?? [];
        HasValueMappings = Fields.Any(static field => field.HasValueMappings);
        ShowOriginalValues = false;
        _loadedItems = [];
        Items = CreateItemsView(_loadedItems);
        SelectedItem = null;
        FilterText = string.Empty;
        HasLoadedItems = false;
        FilterSummary = F("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", 0);
        SetSelection([]);
        RefreshOutputFormats();
        RefreshEffectiveDatabasePath(_workspaceContext.Current);
        ScanCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
        ScheduleAutomaticLoad();
    }

    partial void OnRootPathChanged(string? value)
    {
        _lastAutomaticLoadKey = null;
        ScheduleWorkspacePersistence(WorkspaceDraftFields.RootPath);
    }

    partial void OnDatabasePathChanged(string? value)
    {
        _lastAutomaticLoadKey = null;
        ScheduleWorkspacePersistence(WorkspaceDraftFields.DatabasePath);
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingExistingIndexChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemChanged(CatalogItemViewModel? value)
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        OpenSelectedFolderCommand.NotifyCanExecuteChanged();
        CopySelectedFolderPathCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnShowOriginalValuesChanged(bool value)
    {
        foreach (var item in _loadedItems)
        {
            item.SetShowOriginalValues(value);
        }

        ApplyFilter();
    }

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
            var latestDraft = Volatile.Read(ref _latestWorkspaceDraft);
            var currentVersion = Volatile.Read(ref _workspaceDraftVersion);
            var currentAttempt = latestDraft is not null
                && latestDraft.Version == currentVersion
                    ? latestDraft
                    : null;
            var pendingFields = ReadWorkspaceDirtyFields()
                | (currentAttempt?.Fields ?? WorkspaceDraftFields.None);
            ApplyWorkspaceSnapshot(eventArgs.Current, pendingFields);

            // An event from another page can arrive while a local field is still
            // being edited. Apply its non-dirty fields immediately, but wait to
            // load until the shared snapshot also contains this page's draft.
            if (pendingFields == WorkspaceDraftFields.None
                || currentAttempt is not null
                && DraftFieldsMatch(currentAttempt, eventArgs.Current))
            {
                ScheduleAutomaticLoad();
            }
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
        if (!hadProfiles
            && Profiles.Count > 0
            && StatusTitle == L("Loc.Catalog.Status.NoProfiles", "프로필 없음"))
        {
            SetStatus(
                L("Loc.Common.Ready", "준비"),
                L(
                    "Loc.Catalog.Status.ProfileApplied",
                    "새 프로필이 적용되었습니다. 검색 위치를 선택하세요."),
                InfoBarSeverity.Informational);
            ScheduleAutomaticLoad();
        }
        else if (hadProfiles && Profiles.Count == 0)
        {
            SetStatus(
                L("Loc.Catalog.Status.NoProfiles", "프로필 없음"),
                L(
                    "Loc.Catalog.Status.NoProfiles.Message",
                    "로드된 프로필이 없습니다. 프로필 화면에서 진단을 확인하세요."),
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
    private async Task BrowseRootAsync()
    {
        var selected = _pickerService.PickRootDirectory(RootPath);
        if (selected is not null)
        {
            RootPath = selected;
            await CommitWorkspaceAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BrowseDatabaseAsync()
    {
        var selected = _pickerService.PickDatabasePath(DatabasePath);
        if (selected is not null)
        {
            DatabasePath = selected;
            await CommitWorkspaceAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CommitWorkspaceAsync()
    {
        CancelWorkspacePersistenceDebounce();
        var version = Interlocked.Increment(ref _workspaceDraftVersion);
        try
        {
            var fields = ReadWorkspaceDirtyFields();
            if (fields == WorkspaceDraftFields.None)
            {
                ScheduleAutomaticLoad();
                return;
            }

            var attempt = CreateWorkspaceSaveAttempt(version, fields);
            Volatile.Write(ref _latestWorkspaceDraft, attempt);
            var saved = await PersistWorkspaceDraftAsync(
                    attempt,
                    CancellationToken.None,
                    reportFailure: true)
                .ConfigureAwait(true);
            if (saved)
            {
                ScheduleAutomaticLoad();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            Volatile.Write(ref _latestWorkspaceDraft, null);
            ReportWorkspaceSaveFailure(exception);
        }
    }

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (IsLoadingExistingIndex
            && _operationCoordinator.CurrentKind == ApplicationOperationKind.IndexLoad)
        {
            ProgressMessage = L(
                "Loc.Catalog.Progress.CancelExistingLoad",
                "기존 인덱스 불러오기를 취소하는 중…");
            await _operationCoordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(30))
                .ConfigureAwait(true);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        await RunOperationAsync(
                L(
                    "Loc.Catalog.Operation.Scan",
                    "스캔"),
                CatalogOperationMode.Scan)
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task LoadAsync() => RunOperationAsync(
        L("Loc.Catalog.Operation.LoadExisting", "기존 인덱스에서 불러오기"),
        CatalogOperationMode.ExistingIndex);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ProgressMessage = L("Loc.Common.Cancelling", "작업을 취소하는 중입니다…");
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
            ShowSnackbar(
                L("Loc.Files.Action.OpenFolderFailed", "폴더 열기 실패"),
                exception.Message,
                ControlAppearance.Danger);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelectedFolder()
    {
        if (SelectedItem is not null)
        {
            OpenItem(SelectedItem);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void CopySelectedFolderPath()
    {
        CopyItemFolderPath(SelectedItem);
    }

    [RelayCommand]
    private void CopyItemFolderPath(CatalogItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_clipboardService.TrySetText(item.FullPath, out var errorMessage))
        {
            ShowSnackbar(
                L("Loc.Files.Action.CopyFolderCompleted", "폴더 경로 복사 완료"),
                item.FullPath,
                ControlAppearance.Secondary);
        }
        else
        {
            SetStatus(
                L("Loc.Catalog.Action.CopyFolderFailed", "폴더 경로 복사 실패"),
                errorMessage ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다."),
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void CopySelection()
    {
        var format = SelectedOutputFormat
            ?? SelectionOutputFormatDefaults.CreateFullPathLines(_localizer);
        try
        {
            var outputItems = _selectedItems.Select(item => new SelectionOutputItem(
                item.FullPath,
                item.FullPath,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(item.FullPath)),
                item.OutputValues));
            var text = _outputFormatter.Format(format, outputItems);
            if (!_clipboardService.TrySetText(text, out var errorMessage))
            {
                throw new InvalidOperationException(
                    errorMessage
                    ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다."));
            }

            ShowSnackbar(
                L("Loc.Selection.CopyCompleted", "선택 항목 복사 완료"),
                F(
                    "Loc.Selection.CopyCompleted.Message",
                    "{0:N0}개 항목을 '{1}' 형식으로 복사했습니다.",
                    SelectedCount,
                    format.DisplayName),
                ControlAppearance.Success);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException or ArgumentException)
        {
            _logger.LogWarning(exception, "Could not format selected catalog items.");
            SetStatus(
                L("Loc.Selection.CopyFailed", "선택 항목 복사 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private async Task ExportSelectionAsync()
    {
        var destination = _pickerService.PickCatalogExportPath(
            $"FindEverything-catalog-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");
        if (destination is null)
        {
            return;
        }

        // Capture the UI-owned collection and profile schema before yielding.
        // The writer consumes this immutable snapshot on a background thread for
        // XLSX files and through asynchronous file I/O for CSV files.
        var selectedItems = _selectedItems.ToArray();
        var fields = Fields.OrderBy(static field => field.Order).ToArray();
        var headers = new[] { L("Loc.Catalog.Column.Status", "상태") }
            .Concat(fields.Select(static field => field.Header))
            .Append(L("Loc.Catalog.Column.FolderPath", "폴더 경로"))
            .ToArray();
        var rows = selectedItems
            .Select(item => CreateExportRow(item, fields))
            .ToArray();
        var document = new CatalogSelectionExportDocument(
            headers,
            rows,
            rows.Length);

        try
        {
            await _selectionExporter.ExportAsync(
                    destination.Path,
                    destination.Format,
                    document)
                .ConfigureAwait(true);
            ShowSnackbar(
                L("Loc.Catalog.Export.Completed", "선택 항목 내보내기 완료"),
                F(
                    "Loc.Catalog.Export.Completed.Message",
                    "{0:N0}개 항목을 저장했습니다: {1}",
                    selectedItems.Length,
                    destination.Path),
                ControlAppearance.Success);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            _logger.LogWarning(
                exception,
                "Could not export selected catalog items to {ExportPath}.",
                destination.Path);
            SetStatus(
                L("Loc.Catalog.Export.Failed", "선택 항목 내보내기 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    private static IReadOnlyList<string> CreateExportRow(
        CatalogItemViewModel item,
        IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        var values = new string[fields.Count + 2];
        values[0] = item.CoverageText;
        for (var index = 0; index < fields.Count; index++)
        {
            values[index + 1] = item.ExportDisplayValues.TryGetValue(
                fields[index].FieldId,
                out var value)
                ? value
                : string.Empty;
        }

        values[^1] = item.FullPath;
        return values;
    }

    public void SetSelection(IEnumerable<CatalogItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _selectedItems = items.Distinct().ToArray();
        SelectedCount = _selectedItems.Count;
        CopySelectionCommand.NotifyCanExecuteChanged();
        ExportSelectionCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsBusy && SelectedProfile is not null;

    private bool CanScan() => SelectedProfile is not null && (!IsBusy || IsLoadingExistingIndex);

    private bool CanCancel() => IsBusy;

    private bool CanOpenSelected() => SelectedItem is not null;

    private bool CanCopySelection() => SelectedCount > 0;

    private void RefreshOutputFormats()
    {
        var selectedId = SelectedOutputFormat?.Id;
        OutputFormats = _outputFormatStore.GetApplicable(SelectedProfile?.Id);
        SelectedOutputFormat = OutputFormats.FirstOrDefault(format =>
                string.Equals(format.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? OutputFormats.FirstOrDefault(format => format.IsBuiltIn)
            ?? OutputFormats.FirstOrDefault();
    }

    private void OnOutputFormatsChanged(object? sender, EventArgs eventArgs)
    {
        if (_dispatcher.CheckAccess())
        {
            RefreshOutputFormats();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(RefreshOutputFormats);
        }
    }

    private void OnOperationCoordinatorStateChanged(
        object? sender,
        ApplicationOperationStateChangedEventArgs eventArgs)
    {
        if (eventArgs.CurrentKind is not null)
        {
            return;
        }

        void Apply()
        {
            if (eventArgs.PreviousKind == ApplicationOperationKind.IndexWrite)
            {
                // The same DB path can now represent a newly published generation.
                _lastAutomaticLoadKey = null;
            }

            if (_isActive && !IsBusy)
            {
                ScheduleAutomaticLoad();
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(Apply);
        }
    }

    private async Task RunOperationAsync(
        string operationName,
        CatalogOperationMode mode,
        bool isAutomatic = false)
    {
        var writesIndex = mode == CatalogOperationMode.Scan;
        if (IsBusy)
        {
            return;
        }

        if (_operationCoordinator.IsRunning)
        {
            if (isAutomatic)
            {
                _lastAutomaticLoadKey = null;
                ProgressMessage = L(
                    "Loc.Catalog.Progress.AutoLoadDeferred",
                    "현재 작업이 끝나면 기존 인덱스를 자동으로 불러옵니다.");
                return;
            }

            if (writesIndex
                && _operationCoordinator.CurrentKind == ApplicationOperationKind.IndexLoad)
            {
                // ReplaceAsync below cancels the read and claims the write slot
                // atomically, so another automatic load cannot slip in between.
            }
            else
            {
                SetStatus(
                    L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                    L(
                        "Loc.Catalog.Status.WaitForProfileSave",
                        "프로필 저장이 끝난 뒤 다시 시도하세요."),
                    InfoBarSeverity.Warning);
                return;
            }
        }

        IsLoadingExistingIndex = mode == CatalogOperationMode.ExistingIndex;
        _isAutomaticIndexLoad = isAutomatic && mode == CatalogOperationMode.ExistingIndex;
        _activeOperationMode = mode;
        IsBusy = true;
        IsScanning = mode == CatalogOperationMode.Scan;
        SetStatus(
            operationName,
            F(
                "Loc.Catalog.Operation.Started",
                "{0} 작업을 시작했습니다.",
                operationName),
            InfoBarSeverity.Informational);
        CatalogOperationProgressPump? progress = null;
        CatalogResultSession? resultSession = null;
        CatalogResult? result = null;
        ScanCompletionNotice? completionNotice = null;
        try
        {
            // Capture UI-bound values before the coordinator moves the operation
            // to its background scheduler.
            var workspace = ValidateWorkspace();
            CancelWorkspacePersistenceDebounce();
            var workspaceVersion = Interlocked.Increment(ref _workspaceDraftVersion);
            var operationWorkspaceAttempt = CreateWorkspaceSaveAttempt(
                workspaceVersion,
                WorkspaceDraftFields.Profile
                    | WorkspaceDraftFields.RootPath
                    | ReadWorkspaceDirtyFields());
            Volatile.Write(ref _latestWorkspaceDraft, operationWorkspaceAttempt);
            var request = CreateRequest(workspace);
            resultSession = BeginResultSession();
            progress = CreateProgress(resultSession);
            var operationKind = mode switch
            {
                CatalogOperationMode.Scan => ApplicationOperationKind.IndexWrite,
                _ => ApplicationOperationKind.IndexLoad,
            };
            async Task RunCatalogOperationAsync(CancellationToken cancellationToken)
            {
                try
                {
                    if (mode == CatalogOperationMode.Scan)
                    {
                        EnsureDatabaseDirectory(workspace.DatabasePath);
                    }

                    await PersistWorkspaceAsync(
                            operationWorkspaceAttempt,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    AbandonWorkspaceSaveAttempt(operationWorkspaceAttempt);
                    throw;
                }

                result = mode switch
                {
                    CatalogOperationMode.Scan =>
                        await _catalogService.ScanAndLoadAsync(
                            request,
                            progress,
                            cancellationToken).ConfigureAwait(false),
                    _ => await _catalogService.LoadExistingAsync(
                        request,
                        progress,
                        cancellationToken).ConfigureAwait(false),
                };
            }

            try
            {
                if (writesIndex)
                {
                    await _operationCoordinator.ReplaceAsync(
                            ApplicationOperationKind.IndexLoad,
                            operationKind,
                            RunCatalogOperationAsync)
                        .ConfigureAwait(true);
                }
                else
                {
                    await _operationCoordinator.RunAsync(
                            operationKind,
                            RunCatalogOperationAsync)
                        .ConfigureAwait(true);
                }
            }
            catch
            {
                // The coordinator can reject or cancel an operation before its
                // delegate runs. Do not leave that unpublished workspace draft
                // shielding all later shared-workspace changes.
                AbandonWorkspaceSaveAttempt(operationWorkspaceAttempt);
                throw;
            }

            progress.FlushAndClose();
            completionNotice = ApplyResult(
                result ?? throw new InvalidOperationException(
                    L("Loc.Catalog.Error.NoResult", "카탈로그 결과를 받지 못했습니다.")),
                mode,
                resultSession);
        }
        catch (OperationCanceledException)
        {
            progress?.FlushAndClose();
            var partialResultCount = resultSession?.Items.Count ?? 0;
            var partialResultMessage = CreatePartialResultMessage(partialResultCount);
            SetStatus(
                L("Loc.Common.Cancelled", "취소됨"),
                F(
                    "Loc.Catalog.Operation.Cancelled.Message",
                    "{0} 작업이 취소되었습니다.{1}",
                    operationName,
                    partialResultMessage),
                InfoBarSeverity.Warning);
            ProgressMessage = partialResultCount > 0
                ? F(
                    "Loc.Catalog.Progress.CancelledPartial",
                    "취소됨 · 부분 결과 {0:N0}개",
                    partialResultCount)
                : L("Loc.Common.Cancelled", "취소됨");
            ShowSnackbar(
                L("Loc.Catalog.Operation.Cancelled", "작업 취소"),
                F(
                    "Loc.Catalog.Operation.Cancelled.Snackbar",
                    "{0} 작업을 취소했습니다.{1}",
                    operationName,
                    partialResultMessage),
                ControlAppearance.Caution);
        }
        catch (ApplicationOperationBusyException) when (isAutomatic)
        {
            progress?.FlushAndClose();
            _lastAutomaticLoadKey = null;
            ProgressMessage = L(
                "Loc.Catalog.Progress.AutoLoadDeferred",
                "현재 작업이 끝나면 기존 인덱스를 자동으로 불러옵니다.");
        }
        catch (Exception exception)
        {
            progress?.FlushAndClose();
            _logger.LogError(exception, "{OperationName} operation failed.", operationName);
            var partialResultCount = resultSession?.Items.Count ?? 0;
            var partialResultMessage = CreatePartialResultMessage(partialResultCount);
            var failureMessage = UserFacingExceptionLocalizer.TranslateOperationFailure(
                _localizer,
                exception);
            SetStatus(
                L("Loc.Catalog.Status.Error", "오류"),
                $"{failureMessage}{partialResultMessage}",
                InfoBarSeverity.Error);
            ProgressMessage = partialResultCount > 0
                ? F(
                    "Loc.Catalog.Progress.FailedPartial",
                    "작업 실패 · 부분 결과 {0:N0}개",
                    partialResultCount)
                : L("Loc.Catalog.Progress.Failed", "작업 실패");
            ShowSnackbar(
                F("Loc.Catalog.Operation.Failed", "{0} 실패", operationName),
                $"{failureMessage}{partialResultMessage}",
                ControlAppearance.Danger);
        }
        finally
        {
            progress?.FlushAndClose();
            EndResultSession(resultSession);
            IsScanning = false;
            IsLoadingExistingIndex = false;
            _isAutomaticIndexLoad = false;
            _activeOperationMode = default;
            IsBusy = false;
            if (ApplyPendingProfileSnapshot())
            {
                completionNotice = null;
            }

            if (isAutomatic && _isActive)
            {
                // If the page was briefly unloaded and activated again while the
                // cancellation was still completing, resume the automatic load.
                ScheduleAutomaticLoad();
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
            if (mode == CatalogOperationMode.Scan)
            {
                _scanCompletionNotifier.Notify(completionNotice);
            }
        }
    }

    private void ScheduleAutomaticLoad()
    {
        if (!_isActive
            || IsBusy
            || SelectedProfile is null
            || string.IsNullOrWhiteSpace(RootPath)
            || string.IsNullOrWhiteSpace(DatabasePath))
        {
            return;
        }

        string rootPath;
        string databasePath;
        try
        {
            rootPath = Path.GetFullPath(RootPath);
            databasePath = Path.GetFullPath(DatabasePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(rootPath) || !File.Exists(databasePath))
        {
            return;
        }

        var key = string.Join("|", SelectedProfile.Id, rootPath, databasePath);
        if (string.Equals(_lastAutomaticLoadKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastAutomaticLoadKey = key;
        _ = RunAutomaticLoadAsync(key);
    }

    private async Task RunAutomaticLoadAsync(string key)
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (!_isActive
            || !string.Equals(_lastAutomaticLoadKey, key, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(_lastAutomaticLoadKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _lastAutomaticLoadKey = null;
            }

            return;
        }

        if (IsBusy || _operationCoordinator.IsRunning)
        {
            // A later profile/workspace change can schedule this scope again.
            if (string.Equals(_lastAutomaticLoadKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _lastAutomaticLoadKey = null;
            }
            return;
        }

        await RunOperationAsync(
                L(
                    "Loc.Catalog.Operation.AutomaticLoadExisting",
                    "기존 인덱스 자동 불러오기"),
                CatalogOperationMode.ExistingIndex,
                isAutomatic: true)
            .ConfigureAwait(true);
    }

    private static CatalogRequest CreateRequest(WorkspaceSelection workspace) =>
        new(workspace.ProfileId, workspace.RootPath, workspace.DatabasePath);

    private string CreatePartialResultMessage(int partialResultCount) =>
        partialResultCount > 0
        ? F(
            "Loc.Catalog.Result.PartialRetained",
            " 발견한 부분 결과 {0:N0}개는 목록에 유지됩니다.",
            partialResultCount)
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
            L("Loc.Catalog.Status.ProfileChanged", "프로필 변경됨"),
            L(
                "Loc.Catalog.Status.ProfileChanged.Message",
                "작업 중 프로필 구성이 변경되어 이전 규칙의 결과를 비웠습니다. 다시 불러오세요."),
            InfoBarSeverity.Warning);
        ProgressMessage = L(
            "Loc.Catalog.Progress.ProfileChanged",
            "프로필이 변경되었습니다.");
        return true;
    }

    private CatalogOperationProgressPump CreateProgress(CatalogResultSession session) =>
        new(
            _dispatcher,
            (progress, matchedItems) => ApplyProgress(session, progress, matchedItems),
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
        CatalogOperationProgress? progress,
        IReadOnlyList<CatalogItem> matchedItems)
    {
        if (!ReferenceEquals(_activeResultSession, session)
            || session.Version != Volatile.Read(ref _resultSessionVersion)
            || !ReferenceEquals(_loadedItems, session.Items))
        {
            return;
        }

        if (progress is not null)
        {
            ProgressMessage = FormatProgress(
                progress,
                session.Items.Count + matchedItems.Count);
        }

        if (matchedItems.Count == 0)
        {
            return;
        }

        // Materialize before mutating the backing list. If one item cannot be
        // formatted, the batch is rejected as a whole and the view stays in sync.
        var viewModels = matchedItems.Select(item =>
            CreateItemViewModel(item, session.Fields)).ToList();
        var selectedPath = SelectedItem?.FullPath;
        session.Items.AddRange(viewModels);
        HasLoadedItems = true;
        ApplyFilter(selectedPath);
    }

    private string FormatProgress(
        CatalogOperationProgress progress,
        int matchedCount) =>
        progress.Phase switch
        {
            CatalogOperationPhase.Preparing
                when _activeOperationMode == CatalogOperationMode.Scan =>
                L(
                    "Loc.Catalog.Progress.PreparingScan",
                    "스캔 결과를 DB에 저장하고 구조화 결과를 불러올 준비를 하고 있습니다."),
            CatalogOperationPhase.Preparing =>
                L(
                    "Loc.Catalog.Progress.PreparingExistingIndex",
                    "프로필과 기존 인덱스를 준비하고 있습니다."),
            CatalogOperationPhase.Scanning when progress.ScanProgress is { } scan =>
                F(
                    "Loc.Catalog.Progress.IndexScanning",
                    "폴더 {0:N0}개, 항목 {1:N0}개를 확인했습니다.",
                    scan.Directories,
                    scan.Entries),
            CatalogOperationPhase.Scanning =>
                F(
                    "Loc.Catalog.Progress.DirectoryScanning",
                    "폴더 {0:N0}개를 확인했습니다.",
                    progress.ProcessedItems),
            CatalogOperationPhase.Searching =>
                L(
                    "Loc.Catalog.Progress.SearchingIndex",
                    "인덱스에서 폴더를 조회하고 있습니다."),
            CatalogOperationPhase.Mapping when matchedCount > 0 =>
                F(
                    "Loc.Catalog.Progress.MappingWithMatches",
                    "일치 {0:N0}개 · 경로 {1:N0}개를 판별했습니다.",
                    matchedCount,
                    progress.ProcessedItems),
            CatalogOperationPhase.Mapping =>
                F(
                    "Loc.Catalog.Progress.Mapping",
                    "경로 {0:N0}개를 프로필 규칙으로 판별했습니다.",
                    progress.ProcessedItems),
            _ => F(
                "Loc.Catalog.Progress.Processed",
                "{0:N0}개 항목을 처리했습니다.",
                progress.ProcessedItems),
        };

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
            ProgressMessage = L(
                "Loc.Catalog.Progress.LiveDisplaySuspended",
                "실시간 표시를 일시 중단했습니다. 완료 후 전체 결과를 표시합니다.");
        }
    }

    private ScanCompletionNotice ApplyResult(
        CatalogResult result,
        CatalogOperationMode mode,
        CatalogResultSession resultSession)
    {
        // The default scan now persists and maps in one pass. It still owns the
        // profile-directory exclusion summary even though it no longer uses the
        // legacy non-persistent discovery result.
        var discoveredDirectly = mode == CatalogOperationMode.Scan;
        var selectedPath = SelectedItem?.FullPath;
        var fields = result.Profile.Fields.OrderBy(static field => field.Order).ToArray();
        if (!Fields.SequenceEqual(fields))
        {
            Fields = fields;
            HasValueMappings = Fields.Any(static field => field.HasValueMappings);
        }
        if (!ReferenceEquals(_activeResultSession, resultSession)
            || !ReferenceEquals(_loadedItems, resultSession.Items))
        {
            throw new InvalidOperationException(
                L(
                    "Loc.Catalog.Error.ResultSessionChanged",
                    "구조화 결과 세션이 작업 도중 변경되었습니다."));
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
                CreateItemViewModel(item, fields)));
        }

        _loadedItems = resultSession.Items;
        HasLoadedItems = _loadedItems.Count > 0;
        ApplyFilter(selectedPath);
        ProgressMessage = F(
            "Loc.Catalog.Progress.Loaded",
            "{0:N0}개 항목을 불러왔습니다.",
            _loadedItems.Count);

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
            : F(
                "Loc.Catalog.Result.ScanSummary",
                " · 인덱싱 상태 {0} · 오류 {1:N0}",
                result.ScanReport.Status,
                result.ScanReport.Errors.Count);
        var discoverySummary = result.DiscoveryReport is null
            ? string.Empty
            : F(
                "Loc.Catalog.Result.DiscoverySummary",
                " · 방문 폴더 {0:N0} · 하위 탐색 생략 {1:N0} · 오류 {2:N0}{3}",
                result.DiscoveryReport.Progress.Directories,
                result.DiscoveryReport.Progress.PrunedDirectories,
                result.DiscoveryReport.Progress.ErrorCount,
                discoveredDirectly
                    ? F(
                        "Loc.Catalog.Result.ElapsedSuffix",
                        " · 소요 {0}",
                        FormatElapsed(result.DiscoveryReport.Progress.Elapsed))
                    : string.Empty);
        var exclusionSummary = discoveredDirectly
            ? F(
                "Loc.Catalog.Result.ExclusionSummary",
                " · 이름 규칙 제외 {0:N0}{1}",
                result.ExcludedDirectoryCount,
                exclusionIncomplete
                    ? F(
                        "Loc.Catalog.Result.ExclusionWarningSuffix",
                        " · 제외 규칙 경고 {0:N0}",
                        result.DirectoryExclusionIssues.Count)
                    : string.Empty)
            : string.Empty;
        var statusTitle = incomplete
            ? L("Loc.Catalog.Result.Partial", "부분 결과")
            : mode switch
            {
                CatalogOperationMode.Scan => L(
                    "Loc.Catalog.Result.ScanCompleted",
                    "스캔 및 DB 저장 완료"),
                _ => L(
                    "Loc.Catalog.Result.ExistingLoadCompleted",
                    "기존 인덱스 불러오기 완료"),
            };
        var statusMessage = F(
            "Loc.Catalog.Result.Summary",
            "후보 {0:N0} · 일치 {1:N0} · 규칙 외 {2:N0} · 변환 오류 {3:N0}{4}{5}{6}{7}",
            result.CandidateCount,
            result.Items.Count,
            result.NoMatchCount,
            result.InvalidItems.Count,
            exclusionSummary,
            discoverySummary,
            scanSummary,
            result.HasPendingScopes
                ? L(
                    "Loc.Catalog.Result.PendingScopesSuffix",
                    " · 아직 인덱싱되지 않은 범위가 있습니다.")
                : string.Empty);
        SetStatus(statusTitle, statusMessage, severity);
        var completionElapsedSummary = result.ScanReport is { } scan
            ? F(
                "Loc.Catalog.Result.CompletionElapsedSuffix",
                " 소요 {0}",
                FormatElapsed(scan.Progress.Elapsed))
            : discoveredDirectly && result.DiscoveryReport is { } report
                ? F(
                    "Loc.Catalog.Result.CompletionElapsedSuffix",
                    " 소요 {0}",
                    FormatElapsed(report.Progress.Elapsed))
                : string.Empty;
        return new ScanCompletionNotice(
            statusTitle,
            F(
                "Loc.Catalog.Result.CompletionNotice",
                "프로필 규칙에 맞는 {0:N0}개 폴더를 찾았습니다.{1}{2}",
                result.Items.Count,
                completionElapsedSummary,
                incomplete
                    ? L(
                        "Loc.Catalog.Result.IncompleteSuffix",
                        " 일부 경로는 확인이 필요합니다.")
                    : string.Empty),
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
        FilterSummary = TextFilter.Normalize(FilterText).Length == 0
            ? F("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", _loadedItems.Count)
            : F(
                "Loc.Catalog.Filter.Active",
                "{0:N0} / {1:N0}개 항목",
                Items.Count,
                _loadedItems.Count);
    }

    private ListCollectionView CreateItemsView(List<CatalogItemViewModel> items)
    {
        var view = new ListCollectionView(items)
        {
            Filter = value => value is CatalogItemViewModel item && item.Matches(FilterText),
        };
        return view;
    }

    private CatalogItemViewModel CreateItemViewModel(
        CatalogItem item,
        IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        var viewModel = new CatalogItemViewModel(item, fields, _localizer);
        viewModel.SetShowOriginalValues(ShowOriginalValues);
        return viewModel;
    }

    private WorkspaceSelection ValidateWorkspace()
    {
        if (SelectedProfile is null)
        {
            throw new InvalidOperationException(
                L("Loc.Catalog.Error.SelectProfile", "검색 프로필을 선택하세요."));
        }

        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException(
                L("Loc.Catalog.Error.SelectRoot", "검색할 루트 폴더를 선택하세요."));
        }

        var rootPath = Path.GetFullPath(RootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException(
                F(
                    "Loc.Catalog.Error.RootNotFound",
                    "루트 폴더를 찾을 수 없습니다: {0}",
                    rootPath));
        }

        var databasePath = string.IsNullOrWhiteSpace(DatabasePath)
            ? _databasePathResolver.ResolveProfileDatabasePath(
                SelectedProfile.Id,
                _workspaceContext.Current)
            : Path.GetFullPath(DatabasePath);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);

        return new WorkspaceSelection(SelectedProfile.Id, normalizedRoot, databasePath);
    }

    private async Task PersistWorkspaceAsync(
        WorkspaceSaveAttempt attempt,
        CancellationToken cancellationToken)
    {
        await _workspaceContext.UpdateAsync(
                current => MergeWorkspaceDraft(current, attempt),
                cancellationToken)
            .ConfigureAwait(false);
        CompleteWorkspaceSaveAttempt(attempt);
    }

    private void ApplyWorkspaceSnapshot(
        WorkspaceSnapshot snapshot,
        WorkspaceDraftFields preservedFields = WorkspaceDraftFields.None)
    {
        _isApplyingWorkspaceSnapshot = true;
        try
        {
            if (!preservedFields.HasFlag(WorkspaceDraftFields.RootPath))
            {
                RootPath = snapshot.RootPath;
            }

            if (!preservedFields.HasFlag(WorkspaceDraftFields.DatabasePath))
            {
                DatabasePath = SelectedProfile is null
                    ? null
                    : _databasePathResolver.ResolveProfileDatabasePath(
                        SelectedProfile.Id,
                        snapshot);
            }
        }
        finally
        {
            _isApplyingWorkspaceSnapshot = false;
        }
    }

    private void RefreshEffectiveDatabasePath(WorkspaceSnapshot snapshot)
    {
        var wasApplyingSnapshot = _isApplyingWorkspaceSnapshot;
        _isApplyingWorkspaceSnapshot = true;
        try
        {
            DatabasePath = SelectedProfile is null
                ? null
                : _databasePathResolver.ResolveProfileDatabasePath(
                    SelectedProfile.Id,
                    snapshot);
        }
        finally
        {
            _isApplyingWorkspaceSnapshot = wasApplyingSnapshot;
        }
    }

    private void ScheduleWorkspacePersistence(WorkspaceDraftFields changedField)
    {
        if (_disposed || _isApplyingWorkspaceSnapshot)
        {
            return;
        }

        _ = Interlocked.Or(ref _workspaceDirtyFields, (int)changedField);
        CancelWorkspacePersistenceDebounce();
        var version = Interlocked.Increment(ref _workspaceDraftVersion);
        WorkspaceSaveAttempt attempt;
        try
        {
            attempt = CreateWorkspaceSaveAttempt(version, ReadWorkspaceDirtyFields());
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            // TextBox updates arrive for every keystroke. An incomplete path is
            // not an error until focus is committed; simply wait for the next edit.
            Volatile.Write(ref _latestWorkspaceDraft, null);
            return;
        }

        var source = new CancellationTokenSource();
        Volatile.Write(ref _latestWorkspaceDraft, attempt);
        _workspaceSaveDebounce = source;
        _ = PersistWorkspaceAfterDelayAsync(attempt, source);
    }

    private async Task PersistWorkspaceAfterDelayAsync(
        WorkspaceSaveAttempt attempt,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), source.Token)
                .ConfigureAwait(false);
            _ = await PersistWorkspaceDraftAsync(
                    attempt,
                    source.Token,
                    reportFailure: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // A later keystroke or an explicit operation owns the current draft.
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _workspaceSaveDebounce,
                null,
                source);
            source.Dispose();
        }
    }

    private async Task<bool> PersistWorkspaceDraftAsync(
        WorkspaceSaveAttempt attempt,
        CancellationToken cancellationToken,
        bool reportFailure)
    {
        if (attempt.Version != Volatile.Read(ref _workspaceDraftVersion))
        {
            _ = Interlocked.CompareExchange(
                ref _latestWorkspaceDraft,
                null,
                attempt);
            return false;
        }

        try
        {
            await _workspaceContext.UpdateAsync(
                    current => MergeWorkspaceDraft(current, attempt),
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteWorkspaceSaveAttempt(attempt);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (reportFailure)
        {
            _ = Interlocked.CompareExchange(
                ref _latestWorkspaceDraft,
                null,
                attempt);
            _logger.LogWarning(exception, "Could not persist the catalog workspace draft.");
            if (_dispatcher.CheckAccess())
            {
                ReportWorkspaceSaveFailure(exception);
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _ = _dispatcher.BeginInvoke(() => ReportWorkspaceSaveFailure(exception));
            }

            return false;
        }
    }

    private WorkspaceSaveAttempt CreateWorkspaceSaveAttempt(
        long version,
        WorkspaceDraftFields fields)
    {
        var current = _workspaceContext.Current;
        var normalizedRoot = fields.HasFlag(WorkspaceDraftFields.RootPath)
            ? string.IsNullOrWhiteSpace(RootPath)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath))
            : current.RootPath;
        var profileId = SelectedProfile?.Id ?? current.SelectedProfileId;
        var profileDatabasePaths = new Dictionary<string, string>(
            current.ProfileDatabasePaths
                ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (fields.HasFlag(WorkspaceDraftFields.DatabasePath)
            && !string.IsNullOrWhiteSpace(profileId))
        {
            _ = profileDatabasePaths.Remove(profileId);
            if (!string.IsNullOrWhiteSpace(DatabasePath))
            {
                profileDatabasePaths[profileId] = Path.GetFullPath(DatabasePath);
            }
        }

        return new WorkspaceSaveAttempt(
            version,
            fields,
            new WorkspaceSnapshot(
                profileId,
                normalizedRoot,
                current.DatabasePath,
                profileDatabasePaths));
    }

    private static WorkspaceSnapshot MergeWorkspaceDraft(
        WorkspaceSnapshot current,
        WorkspaceSaveAttempt attempt) =>
        new(
            attempt.Fields.HasFlag(WorkspaceDraftFields.Profile)
                ? attempt.Snapshot.SelectedProfileId
                : current.SelectedProfileId,
            attempt.Fields.HasFlag(WorkspaceDraftFields.RootPath)
                ? attempt.Snapshot.RootPath
                : current.RootPath,
            current.DatabasePath,
            attempt.Fields.HasFlag(WorkspaceDraftFields.DatabasePath)
                ? MergeProfileDatabaseOverride(current, attempt.Snapshot)
                : current.ProfileDatabasePaths);

    private static IReadOnlyDictionary<string, string>? MergeProfileDatabaseOverride(
        WorkspaceSnapshot current,
        WorkspaceSnapshot requested)
    {
        var profileId = requested.SelectedProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return current.ProfileDatabasePaths;
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (current.ProfileDatabasePaths is not null)
        {
            foreach (var (existingProfileId, path) in current.ProfileDatabasePaths)
            {
                merged[existingProfileId] = path;
            }
        }

        _ = merged.Remove(profileId);
        var requestedPath = GetProfileDatabaseOverride(requested, profileId);
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            merged[profileId] = requestedPath;
        }

        return merged;
    }

    private static bool DraftFieldsMatch(
        WorkspaceSaveAttempt attempt,
        WorkspaceSnapshot current) =>
        (!attempt.Fields.HasFlag(WorkspaceDraftFields.Profile)
            || string.Equals(
                attempt.Snapshot.SelectedProfileId,
                current.SelectedProfileId,
                StringComparison.OrdinalIgnoreCase))
        && (!attempt.Fields.HasFlag(WorkspaceDraftFields.RootPath)
            || PathsEqual(attempt.Snapshot.RootPath, current.RootPath))
        && (!attempt.Fields.HasFlag(WorkspaceDraftFields.DatabasePath)
            || PathsEqual(
                GetProfileDatabaseOverride(
                    attempt.Snapshot,
                    attempt.Snapshot.SelectedProfileId),
                GetProfileDatabaseOverride(
                    current,
                    attempt.Snapshot.SelectedProfileId)));

    private static string? GetProfileDatabaseOverride(
        WorkspaceSnapshot snapshot,
        string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)
            || snapshot.ProfileDatabasePaths is null)
        {
            return null;
        }

        return snapshot.ProfileDatabasePaths
            .Where(pair => string.Equals(
                pair.Key,
                profileId,
                StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Value)
            .FirstOrDefault();
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private WorkspaceDraftFields ReadWorkspaceDirtyFields() =>
        (WorkspaceDraftFields)Volatile.Read(ref _workspaceDirtyFields);

    private void CompleteWorkspaceSaveAttempt(WorkspaceSaveAttempt attempt)
    {
        var isCurrent = attempt.Version == Volatile.Read(ref _workspaceDraftVersion);
        if (isCurrent)
        {
            _ = Interlocked.And(ref _workspaceDirtyFields, ~(int)attempt.Fields);
        }

        AbandonWorkspaceSaveAttempt(attempt);
        if (!isCurrent
            || !attempt.Fields.HasFlag(WorkspaceDraftFields.DatabasePath))
        {
            return;
        }

        void Refresh()
        {
            if (attempt.Version == Volatile.Read(ref _workspaceDraftVersion)
                && !ReadWorkspaceDirtyFields().HasFlag(WorkspaceDraftFields.DatabasePath))
            {
                RefreshEffectiveDatabasePath(_workspaceContext.Current);
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(Refresh);
        }
    }

    private void AbandonWorkspaceSaveAttempt(WorkspaceSaveAttempt attempt) =>
        _ = Interlocked.CompareExchange(
            ref _latestWorkspaceDraft,
            null,
            attempt);

    private void CancelWorkspacePersistenceDebounce()
    {
        var source = Interlocked.Exchange(ref _workspaceSaveDebounce, null);
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delayed writer completed while the edit was being committed.
        }
    }

    private void ReportWorkspaceSaveFailure(Exception exception) => SetStatus(
        L("Loc.Catalog.Status.WorkspaceSaveFailed", "작업 위치 저장 실패"),
        exception.Message,
        InfoBarSeverity.Error);

    private void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                L(
                    "Loc.Catalog.Error.DatabaseParentMissing",
                    "데이터베이스의 상위 폴더를 확인할 수 없습니다."));
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

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private string F(
        string key,
        string koreanFallback,
        params object?[] arguments) =>
        _localizer.Format(key, koreanFallback, arguments);

    private string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return F(
                "Loc.Catalog.Elapsed.Hours",
                "{0:0}시간 {1:00}분 {2:00.00}초",
                (long)elapsed.TotalHours,
                elapsed.Minutes,
                elapsed.Seconds + elapsed.Milliseconds / 1000d);
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return F(
                "Loc.Catalog.Elapsed.Minutes",
                "{0:0}분 {1:00.00}초",
                (long)elapsed.TotalMinutes,
                elapsed.Seconds + elapsed.Milliseconds / 1000d);
        }

        return F(
            "Loc.Catalog.Elapsed.Seconds",
            "{0:0.00}초",
            elapsed.TotalSeconds);
    }

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
        private readonly Action<CatalogOperationProgress?, IReadOnlyList<CatalogItem>> _applyProgress;
        private readonly Action<Exception> _reportFailure;
        private CatalogOperationProgress? _latestProgress;
        private bool _drainScheduled;
        private bool _closed;

        public CatalogOperationProgressPump(
            Dispatcher dispatcher,
            Action<CatalogOperationProgress?, IReadOnlyList<CatalogItem>> applyProgress,
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

                _latestProgress = value;
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
            CatalogOperationProgress? progress;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                batch = DrainPendingItems(int.MaxValue);
                progress = _latestProgress;
                _latestProgress = null;
                _drainScheduled = false;
            }

            _drainTimer.Stop();
            _drainTimer.Tick -= OnDrainTimerTick;
            ApplySafely(progress, batch);
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
            CatalogOperationProgress? progress;
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
                progress = _latestProgress;
                _latestProgress = null;
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

            ApplySafely(progress, batch);
        }

        private void ApplySafely(
            CatalogOperationProgress? progress,
            IReadOnlyList<CatalogItem> batch)
        {
            try
            {
                _applyProgress(progress, batch);
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
                _latestProgress = null;
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

    private sealed record WorkspaceSaveAttempt(
        long Version,
        WorkspaceDraftFields Fields,
        WorkspaceSnapshot Snapshot);

    [Flags]
    private enum WorkspaceDraftFields
    {
        None = 0,
        Profile = 1,
        RootPath = 2,
        DatabasePath = 4,
        All = Profile | RootPath | DatabasePath,
    }

    private enum CatalogOperationMode
    {
        Scan = 0,
        ExistingIndex = 1,
    }
}
