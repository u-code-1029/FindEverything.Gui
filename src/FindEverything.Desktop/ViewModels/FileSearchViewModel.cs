using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class FileSearchViewModel : ObservableObject, IDisposable
{
    private const int UnlimitedSizeStep = 11;
    private readonly IFileSearchService _fileSearchService;
    private readonly IValidatedSettingsState<IndexingOptions> _indexingSettings;
    private readonly IWorkspaceContext _workspaceContext;
    private readonly IIndexDatabasePathResolver _databasePathResolver;
    private readonly IDesktopPickerService _pickerService;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly IScanCompletionNotifier _scanCompletionNotifier;
    private readonly ISnackbarService _snackbarService;
    private readonly ISelectionOutputFormatStore _outputFormatStore;
    private readonly ISelectionOutputFormatter _outputFormatter;
    private readonly IClipboardService _clipboardService;
    private readonly IProfileCatalog _profileCatalog;
    private readonly IProfilePathCanonicalizer _profilePathCanonicalizer;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<FileSearchViewModel> _logger;
    private readonly IUiScheduler _uiScheduler;
    private CancellationTokenSource? _queryCancellation;
    private Task? _activeLoadTask;
    private long _queryVersion;
    private long _scopeVersion;
    private IndexRootStatus? _loadedSearchSnapshot;
    private bool _coercingSizeRange;
    private bool _disposed;
    private IReadOnlyList<FileSearchItemViewModel> _selectedItems = [];

    public FileSearchViewModel(
        IFileSearchService fileSearchService,
        IValidatedSettingsState<IndexingOptions> indexingSettings,
        IWorkspaceContext workspaceContext,
        IIndexDatabasePathResolver databasePathResolver,
        IDesktopPickerService pickerService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        IScanCompletionNotifier scanCompletionNotifier,
        ISnackbarService snackbarService,
        ISelectionOutputFormatStore outputFormatStore,
        ISelectionOutputFormatter outputFormatter,
        IClipboardService clipboardService,
        IProfileCatalog profileCatalog,
        IProfilePathCanonicalizer profilePathCanonicalizer,
        IAppLocalizer localizer,
        ILogger<FileSearchViewModel> logger,
        IUiScheduler? uiScheduler = null)
    {
        _fileSearchService = fileSearchService;
        _indexingSettings = indexingSettings;
        _workspaceContext = workspaceContext;
        _databasePathResolver = databasePathResolver;
        _pickerService = pickerService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _scanCompletionNotifier = scanCompletionNotifier;
        _snackbarService = snackbarService;
        _outputFormatStore = outputFormatStore;
        _outputFormatter = outputFormatter;
        _clipboardService = clipboardService;
        _profileCatalog = profileCatalog;
        _profilePathCanonicalizer = profilePathCanonicalizer;
        _localizer = localizer;
        _logger = logger;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;

        KindChoices =
        [
            new(null, L("Loc.Files.Kind.All", "전체")),
            new(IndexedPathKind.File, L("Loc.Files.Kind.File", "파일")),
            new(IndexedPathKind.Directory, L("Loc.Files.Kind.Folder", "폴더")),
        ];
        SortChoices =
        [
            new(EntrySortField.Name, L("Loc.Files.Sort.Name", "이름")),
            new(EntrySortField.Path, L("Loc.Files.Sort.Path", "경로")),
            new(EntrySortField.FileType, L("Loc.Files.Sort.Kind", "종류")),
            new(EntrySortField.Size, L("Loc.Files.Sort.Size", "크기")),
            new(EntrySortField.Modified, L("Loc.Files.Sort.Modified", "수정일")),
            new(EntrySortField.Created, L("Loc.Files.Sort.Created", "생성일")),
        ];
        DateFilterChoices =
        [
            new(DateFilterPreset.Any, L("Loc.Files.Date.Any", "전체 기간")),
            new(DateFilterPreset.Today, L("Loc.Files.Date.Today", "오늘")),
            new(DateFilterPreset.LastSevenDays, L("Loc.Files.Date.LastSeven", "최근 7일")),
            new(DateFilterPreset.LastThirtyDays, L("Loc.Files.Date.LastThirty", "최근 30일")),
            new(DateFilterPreset.Custom, L("Loc.Files.Date.Custom", "직접 지정")),
        ];

        SelectedKind = KindChoices[0];
        SelectedSort = SortChoices[0];
        SelectedCreatedDateFilter = DateFilterChoices[0];
        SelectedModifiedDateFilter = DateFilterChoices[0];
        var workspace = workspaceContext.Current;
        RootPath = workspace.RootPath;
        DatabasePath = databasePathResolver.ResolveFileSearchDatabasePath(workspace);
        IsUsingDefaultDatabasePath = string.IsNullOrWhiteSpace(workspace.DatabasePath);
        IndexStatusTitle = L("Loc.Files.Status.ChooseRoot", "검색 위치를 선택하세요");
        IndexStatusDetail = L(
            "Loc.Files.Status.ChooseRoot.Detail",
            "선택한 위치만 색인하며 원본 파일은 변경하지 않습니다.");
        _workspaceContext.Changed += OnWorkspaceChanged;
        _outputFormatStore.Changed += OnOutputFormatsChanged;
        _profileCatalog.Changed += OnProfilesChanged;
        RefreshOutputFormats();
        _ = InitializeAsync();
    }

    [ObservableProperty]
    private IList<FileSearchItemViewModel> _items = new ObservableCollection<FileSearchItemViewModel>();
    private IResultQuerySession<IndexedPathEntry>? _fileQuery;
    private FileSearchScope? _fileQueryScope;
    private EntrySearchRequest? _fileQueryRequest;
    private readonly QuerySelection _selection = new();
    private int _selectionAnchor = -1;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _copyCancellation;
    private CancellationTokenSource? _selectionCancellation;

    [ObservableProperty]
    private bool _isCopyingSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary))]
    private bool _isResultCountPending;

    public IReadOnlyList<EntryKindChoice> KindChoices { get; }

    public IReadOnlyList<EntrySortChoice> SortChoices { get; }

    public IReadOnlyList<DateFilterChoice> DateFilterChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    [NotifyPropertyChangedFor(nameof(HasNoRoot))]
    [NotifyPropertyChangedFor(nameof(IsSetupStateVisible))]
    [NotifyCanExecuteChangedFor(nameof(RefreshIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseRefreshScopeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshScopeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshItemScopeCommand))]
    private string? _rootPath;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshScopeCommand))]
    private string? _refreshScopePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDefaultDatabasePathCommand))]
    private bool _isUsingDefaultDatabasePath;

    public string DefaultDatabasePath => _databasePathResolver.ResolveFileSearchDatabasePath(
        new WorkspaceSnapshot(null, null, null));

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private EntryKindChoice _selectedKind = null!;

    [ObservableProperty]
    private EntrySortChoice _selectedSort = null!;

    [ObservableProperty]
    private EntrySortDirection _sortDirection = EntrySortDirection.Ascending;

    [ObservableProperty]
    private DateFilterChoice _selectedCreatedDateFilter = null!;

    [ObservableProperty]
    private DateFilterChoice _selectedModifiedDateFilter = null!;

    [ObservableProperty]
    private DateTime? _createdFromDate;

    [ObservableProperty]
    private DateTime? _createdToDate;

    [ObservableProperty]
    private DateTime? _modifiedFromDate;

    [ObservableProperty]
    private DateTime? _modifiedToDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimumSizeText))]
    private double _minimumSizeStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximumSizeText))]
    private double _maximumSizeStep = UnlimitedSizeStep;

    [ObservableProperty]
    private FileSearchItemViewModel? _selectedItem;

    [ObservableProperty]
    private IReadOnlyList<SelectionOutputFormatDefinition> _outputFormats = [];

    [ObservableProperty]
    private SelectionOutputFormatDefinition? _selectedOutputFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupStateVisible))]
    [NotifyPropertyChangedFor(nameof(IsNoResultsVisible))]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(nameof(BrowseRefreshScopeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshScopeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshItemScopeCommand))]
    private bool _hasIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupStateVisible))]
    [NotifyPropertyChangedFor(nameof(IsNoResultsVisible))]
    private bool _hasSearched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoResultsVisible))]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private bool _hasPendingScopes;

    [ObservableProperty]
    private bool _isFilterOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _indexStatusTitle = "검색 위치를 선택하세요";

    [ObservableProperty]
    private string _indexStatusDetail = "선택한 위치만 색인하며 원본 파일은 변경하지 않습니다.";

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public bool HasRoot => !string.IsNullOrWhiteSpace(RootPath);

    public bool HasNoRoot => !HasRoot;

    public bool CanSearch => HasIndex;

    public bool IsSetupStateVisible => !HasRoot || !HasIndex;

    public bool IsNoResultsVisible => HasIndex && HasSearched && !IsSearching && Items.Count == 0;

    public bool HasSelection => SelectedCount > 0;

    public string MinimumSizeText => MinimumSizeStep <= 0
        ? L("Loc.Files.Size.Unlimited", "제한 없음")
        : FileSizeFormatter.Format(SizeStepToBytes(MinimumSizeStep) ?? 0);

    public string MaximumSizeText => MaximumSizeStep >= UnlimitedSizeStep
        ? L("Loc.Files.Size.Unlimited", "제한 없음")
        : FileSizeFormatter.Format(SizeStepToBytes(MaximumSizeStep) ?? 0);

    public string ResultSummary => HasSearched
        ? IsResultCountPending
            ? F("Loc.Files.Results.CountPending", "{0:N0}개 로드됨 · 전체 개수 확인 중",
                Items is VirtualResultCollection<FileSearchItemViewModel> pendingItems ? pendingItems.CachedItemCount : Items.Count)
            : Items is VirtualResultCollection<FileSearchItemViewModel> virtualItems
            ? F("Loc.Files.Results.CachedSummary", "{0:N0}개 결과 · {1:N0}개 캐시됨", TotalCount, virtualItems.CachedItemCount)
            : F("Loc.Files.Results.Summary", "{0:N0}개 결과 · {1:N0}개 표시", TotalCount, Items.Count)
        : L("Loc.Files.Results.Ready", "검색 준비");

    public string SortDirectionText => SortDirection == EntrySortDirection.Ascending
        ? L("Loc.Files.Sort.Ascending", "오름차순")
        : L("Loc.Files.Sort.Descending", "내림차순");

    public string DetailsButtonText => IsDetailsOpen
        ? L("Loc.Common.Compact", "간단히")
        : L("Loc.Common.Details", "자세히");

    public bool IsCreatedCustomRange => SelectedCreatedDateFilter.Value == DateFilterPreset.Custom;

    public bool IsModifiedCustomRange => SelectedModifiedDateFilter.Value == DateFilterPreset.Custom;

    public string ResultSourceDescription
    {
        get
        {
            var scope = _fileQueryScope;
            if (scope is null) return string.Empty;
            var published = _loadedSearchSnapshot?.LastPublishedUtc?.LocalDateTime.ToString("g")
                ?? L("Loc.Common.TimeUnavailable", "시간 정보 없음");
            return F("Loc.Files.Results.Source", "{0} · DB: {1} · 갱신: {2}", scope.RootPath, scope.DatabasePath, published);
        }
    }

    partial void OnRootPathChanged(string? oldValue, string? newValue)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
        {
            RefreshScopePath = null;
        }
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnSelectedKindChanged(EntryKindChoice value) => ScheduleSearch();

    partial void OnSelectedSortChanged(EntrySortChoice value) => ScheduleSearch(preserveSelection: true);

    partial void OnSortDirectionChanged(EntrySortDirection value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        ScheduleSearch(preserveSelection: true);
    }

    partial void OnSelectedCreatedDateFilterChanged(DateFilterChoice value)
    {
        OnPropertyChanged(nameof(IsCreatedCustomRange));
        ScheduleSearch();
    }

    partial void OnSelectedModifiedDateFilterChanged(DateFilterChoice value)
    {
        OnPropertyChanged(nameof(IsModifiedCustomRange));
        ScheduleSearch();
    }

    partial void OnCreatedFromDateChanged(DateTime? value) => ScheduleCustomDateSearch(IsCreatedCustomRange);

    partial void OnCreatedToDateChanged(DateTime? value) => ScheduleCustomDateSearch(IsCreatedCustomRange);

    partial void OnModifiedFromDateChanged(DateTime? value) => ScheduleCustomDateSearch(IsModifiedCustomRange);

    partial void OnModifiedToDateChanged(DateTime? value) => ScheduleCustomDateSearch(IsModifiedCustomRange);

    partial void OnMinimumSizeStepChanged(double value)
    {
        if (!_coercingSizeRange && value > MaximumSizeStep && MaximumSizeStep < UnlimitedSizeStep)
        {
            _coercingSizeRange = true;
            MaximumSizeStep = value;
            _coercingSizeRange = false;
        }

        ScheduleSearch();
    }

    partial void OnMaximumSizeStepChanged(double value)
    {
        if (!_coercingSizeRange && value < MinimumSizeStep)
        {
            _coercingSizeRange = true;
            MinimumSizeStep = value;
            _coercingSizeRange = false;
        }

        ScheduleSearch();
    }

    partial void OnTotalCountChanged(long value) => OnPropertyChanged(nameof(ResultSummary));

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(ResultSummary));

    partial void OnSelectedItemChanged(FileSearchItemViewModel? value)
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        ShowSelectedInFolderCommand.NotifyCanExecuteChanged();
        CopySelectedPathCommand.NotifyCanExecuteChanged();
        OpenSelectedFolderCommand.NotifyCanExecuteChanged();
        CopySelectedFolderPathCommand.NotifyCanExecuteChanged();
        RefreshItemScopeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        BrowseRootCommand.NotifyCanExecuteChanged();
        BrowseDatabaseCommand.NotifyCanExecuteChanged();
        BrowseRefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshIndexCommand.NotifyCanExecuteChanged();
        RefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshItemScopeCommand.NotifyCanExecuteChanged();
        UseDefaultDatabasePathCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSearch));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        LoadMoreCommand.NotifyCanExecuteChanged();
        CopySelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCopyingSelectionChanged(bool value) => CopySelectionCommand.NotifyCanExecuteChanged();

    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand(CanExecute = nameof(CanBrowseRefreshScope))]
    private void BrowseRefreshScope()
    {
        var selected = _pickerService.PickRootDirectory(
            Directory.Exists(RefreshScopePath) ? RefreshScopePath : RootPath);
        if (selected is not null)
        {
            RefreshScopePath = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshScope))]
    private Task RefreshScopeAsync() => RefreshScopeCoreAsync(RefreshScopePath);

    [RelayCommand(CanExecute = nameof(CanRefreshItemScope))]
    private Task RefreshItemScopeAsync(FileSearchItemViewModel? item) =>
        RefreshScopeCoreAsync(item?.FolderPath);

    [RelayCommand(CanExecute = nameof(CanChangeWorkspace))]
    private async Task BrowseRootAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            ShowStatus(
                L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                L("Loc.Files.Status.WaitToChangeRoot", "현재 작업이 끝난 뒤 검색 위치를 변경하세요."),
                InfoBarSeverity.Warning);
            return;
        }

        var selected = _pickerService.PickRootDirectory(RootPath);
        if (selected is null)
        {
            return;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
            await _workspaceContext.UpdateAsync(
                current => current with { RootPath = normalized },
                CancellationToken.None).ConfigureAwait(true);
            InvalidateScope();
            RootPath = normalized;
            ClearSearchResults();
            await StartLoadAsync(refreshStatus: true, debounce: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not change the file-search root.");
            ShowStatus(
                L("Loc.Files.Status.ChangeRootFailed", "검색 위치 변경 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeWorkspace))]
    private async Task BrowseDatabaseAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            ShowStatus(
                L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                L("Loc.Files.Status.WaitToChangeDatabase", "현재 작업이 끝난 뒤 인덱스 위치를 변경하세요."),
                InfoBarSeverity.Warning);
            return;
        }

        var selected = _pickerService.PickDatabasePath(DatabasePath);
        if (selected is null)
        {
            return;
        }

        try
        {
            var normalized = Path.GetFullPath(selected);
            await _workspaceContext.UpdateAsync(
                current => current with { DatabasePath = normalized },
                CancellationToken.None).ConfigureAwait(true);
            InvalidateScope();
            DatabasePath = normalized;
            ClearSearchResults();
            await StartLoadAsync(refreshStatus: true, debounce: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not change the file-search database.");
            ShowStatus(
                L("Loc.Files.Status.ChangeDatabaseFailed", "인덱스 위치 변경 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDefaultDatabasePath))]
    private async Task UseDefaultDatabasePathAsync()
    {
        try
        {
            await _workspaceContext.UpdateAsync(
                current => current with { DatabasePath = null },
                CancellationToken.None).ConfigureAwait(true);
            InvalidateScope();
            var workspace = _workspaceContext.Current;
            DatabasePath = _databasePathResolver.ResolveFileSearchDatabasePath(workspace);
            IsUsingDefaultDatabasePath = true;
            ClearSearchResults();
            await StartLoadAsync(refreshStatus: true, debounce: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not restore the default file-search database.");
            ShowStatus(
                L("Loc.Files.Status.ChangeDatabaseFailed", "인덱스 위치 변경 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshIndex))]
    private async Task RefreshIndexAsync()
    {
        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var refreshResults = false;
        ScanCompletionNotice? completionNotice = null;
        IsBusy = true;
        try
        {
            if (_operationCoordinator.IsRunning)
            {
                if (_operationCoordinator.CurrentKind == ApplicationOperationKind.IndexLoad)
                {
                    ProgressMessage = L(
                        "Loc.Files.Progress.CancelIndexLoad",
                        "기존 인덱스 불러오기를 취소하는 중…");
                    await _operationCoordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(30), scanCancellation.Token)
                        .ConfigureAwait(true);
                }
                else
                {
                    ShowStatus(
                        L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                        L("Loc.Common.TryAfterOperation", "현재 작업이 끝난 뒤 다시 시도하세요."),
                        InfoBarSeverity.Warning);
                    return;
                }
            }

            FileSearchScope scope;
            try
            {
                scope = await Task.Run(() => ValidateScope(), scanCancellation.Token).WaitAsync(scanCancellation.Token).ConfigureAwait(true);
                scanCancellation.Token.ThrowIfCancellationRequested();
                await Task.Run(() => EnsureDatabaseDirectory(scope.DatabasePath), scanCancellation.Token)
                    .WaitAsync(scanCancellation.Token).ConfigureAwait(true);
                await _workspaceContext.UpdateAsync(
                    current => current with
                    {
                        RootPath = scope.RootPath,
                    },
                    scanCancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ShowStatus(
                    L("Loc.Files.Status.CheckScope", "검색 위치 확인 필요"),
                    exception.Message,
                    InfoBarSeverity.Error);
                return;
            }

            // IsBusy is already true, so filter changes cannot start another read
            // between cancelling this session and atomically claiming IndexWrite.
            await CancelActiveLoadAsync().WaitAsync(scanCancellation.Token).ConfigureAwait(true);
            scanCancellation.Token.ThrowIfCancellationRequested();
            ProgressMessage = L("Loc.Files.Progress.PreparingIndex", "인덱스를 준비하고 있습니다…");
            var progress = new Progress<IndexScanProgress>(value =>
            {
                if (!ReferenceEquals(_scanCancellation, scanCancellation) || scanCancellation.IsCancellationRequested) return;
                ProgressMessage = F(
                    "Loc.Files.Progress.Scanning",
                    "{0:N0}개 항목 · {1:N0}개 폴더",
                    value.Entries,
                    value.Directories);
            });
            IndexScanReport? report = null;
            await _operationCoordinator.ReplaceAsync(
                ApplicationOperationKind.IndexLoad,
                ApplicationOperationKind.IndexWrite,
                async cancellationToken =>
                {
                    report = await _fileSearchService.ScanAsync(scope, progress, cancellationToken)
                        .ConfigureAwait(false);
                }, scanCancellation.Token).ConfigureAwait(true);

            if (report is null)
            {
                throw new InvalidOperationException(L(
                    "Loc.Files.Error.NoIndexResult",
                    "인덱싱 결과를 받지 못했습니다."));
            }

            var incomplete = report.Status != IndexScanStatus.Completed || report.Errors.Count > 0;
            ShowStatus(
                incomplete
                    ? L("Loc.Files.Index.Partial", "일부 항목 색인 완료")
                    : L("Loc.Files.Index.Completed", "인덱스 새로 고침 완료"),
                F(
                    "Loc.Files.Index.Result",
                    "{0:N0}개 항목을 확인했습니다. 오류 {1:N0}개",
                    report.Progress.Entries,
                    report.Errors.Count),
                incomplete ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            _snackbarService.Show(
                L("Loc.Files.Index.Completed", "인덱스 새로 고침 완료"),
                F(
                    "Loc.Files.Index.Searchable",
                    "{0:N0}개 항목을 검색할 수 있습니다.",
                    report.Progress.Entries),
                incomplete ? ControlAppearance.Caution : ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(4));
            refreshResults = await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(true)
                && HasIndex;
            completionNotice = new ScanCompletionNotice(
                incomplete
                    ? L("Loc.Files.Index.Partial", "일부 항목 색인 완료")
                    : L("Loc.Files.Index.Completed", "인덱스 새로 고침 완료"),
                F(
                    "Loc.Files.Index.Searchable",
                    "{0:N0}개 항목을 검색할 수 있습니다.",
                    report.Progress.Entries)
                    + (incomplete
                        ? F(
                            "Loc.Files.Index.ErrorsSuffix",
                            " 오류 {0:N0}개를 확인하세요.",
                            report.Errors.Count)
                        : string.Empty),
                incomplete);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(
                L("Loc.Files.Index.Cancelled", "인덱싱 취소"),
                L("Loc.Files.Index.Cancelled.Detail", "기존에 게시된 인덱스는 그대로 유지됩니다."),
                InfoBarSeverity.Warning);
            ProgressMessage = L("Loc.Common.Cancelled", "취소됨");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not refresh the file index.");
            ShowStatus(
                L("Loc.Files.Index.Failed", "인덱싱 실패"),
                UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception),
                InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
            IsBusy = false;
        }

        if (completionNotice is not null)
        {
            _scanCompletionNotifier.Notify(completionNotice);
        }

        if (refreshResults)
        {
            await StartLoadAsync(refreshStatus: false, debounce: false).ConfigureAwait(true);
        }
    }

    private async Task RefreshScopeCoreAsync(string? requestedPath)
    {
        if (IsBusy)
        {
            return;
        }

        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var refreshResults = false;
        ScanCompletionNotice? completionNotice = null;
        IsBusy = true;
        try
        {
            if (_operationCoordinator.IsRunning)
            {
                if (_operationCoordinator.CurrentKind == ApplicationOperationKind.IndexLoad)
                {
                    ProgressMessage = L(
                        "Loc.Files.Progress.CancelIndexLoad",
                        "기존 인덱스 불러오기를 취소하는 중…");
                    await _operationCoordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(30), scanCancellation.Token)
                        .ConfigureAwait(true);
                }
                else
                {
                    ShowStatus(
                        L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                        L("Loc.Common.TryAfterOperation", "현재 작업이 끝난 뒤 다시 시도하세요."),
                        InfoBarSeverity.Warning);
                    return;
                }
            }

            FileSearchScope scope;
            string refreshPath;
            try
            {
                scope = await Task.Run(() => ValidateScope(), scanCancellation.Token).WaitAsync(scanCancellation.Token).ConfigureAwait(true);
                refreshPath = await Task.Run(() => ValidateRefreshScopePath(scope, requestedPath), scanCancellation.Token)
                    .WaitAsync(scanCancellation.Token).ConfigureAwait(true);
                if (!HasIndex)
                {
                    throw new InvalidOperationException(L(
                        "Loc.Files.PartialRefresh.Error.IndexUnavailable",
                        "부분 새로 고침 전에 전체 인덱스를 한 번 만들어 주세요."));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or DirectoryNotFoundException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                ShowStatus(
                    L("Loc.Files.PartialRefresh.CheckPath", "새로 고칠 폴더 확인 필요"),
                    exception.Message,
                    InfoBarSeverity.Error);
                return;
            }

            RefreshScopePath = refreshPath;
            await CancelActiveLoadAsync().WaitAsync(scanCancellation.Token).ConfigureAwait(true);
            scanCancellation.Token.ThrowIfCancellationRequested();
            ProgressMessage = F(
                "Loc.Files.PartialRefresh.Progress.Preparing",
                "'{0}' 부분 새로 고침을 준비하고 있습니다…",
                refreshPath);
            var progress = new Progress<IndexScanProgress>(value =>
            {
                if (!ReferenceEquals(_scanCancellation, scanCancellation) || scanCancellation.IsCancellationRequested) return;
                ProgressMessage = F(
                    "Loc.Files.PartialRefresh.Progress.Scanning",
                    "선택한 폴더에서 {0:N0}개 항목 · {1:N0}개 폴더",
                    value.Entries,
                    value.Directories);
            });
            IndexScanReport? report = null;
            await _operationCoordinator.ReplaceAsync(
                ApplicationOperationKind.IndexLoad,
                ApplicationOperationKind.IndexWrite,
                async cancellationToken =>
                {
                    report = await _fileSearchService.RefreshScopeAsync(
                            scope,
                            refreshPath,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }, scanCancellation.Token).ConfigureAwait(true);

            if (report is null)
            {
                throw new InvalidOperationException(L(
                    "Loc.Files.Error.NoIndexResult",
                    "인덱싱 결과를 받지 못했습니다."));
            }

            var incomplete = report.Status != IndexScanStatus.Completed || report.Errors.Count > 0;
            var title = incomplete
                ? L("Loc.Files.PartialRefresh.Partial", "부분 새로 고침 일부 완료")
                : L("Loc.Files.PartialRefresh.Completed", "부분 새로 고침 완료");
            var message = F(
                "Loc.Files.PartialRefresh.Result",
                "'{0}' 아래에서 {1:N0}개 항목을 확인했습니다. 오류 {2:N0}개",
                refreshPath,
                report.Progress.Entries,
                report.Errors.Count);
            ShowStatus(
                title,
                message,
                incomplete ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            _snackbarService.Show(
                title,
                message,
                incomplete ? ControlAppearance.Caution : ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(4));
            refreshResults = await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(true)
                && HasIndex;
            completionNotice = new ScanCompletionNotice(title, message, incomplete);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(
                L("Loc.Files.PartialRefresh.Cancelled", "부분 새로 고침 취소"),
                L(
                    "Loc.Files.PartialRefresh.Cancelled.Detail",
                    "기존에 게시된 인덱스는 그대로 유지됩니다."),
                InfoBarSeverity.Warning);
            ProgressMessage = L("Loc.Common.Cancelled", "취소됨");
        }
        catch (FileSearchIndexUnavailableException exception)
        {
            _logger.LogWarning(exception, "Could not refresh a file-index subtree.");
            ShowStatus(
                L("Loc.Files.PartialRefresh.Failed", "부분 새로 고침 실패"),
                L(
                    "Loc.Files.PartialRefresh.Error.IndexUnavailable",
                    "부분 새로 고침 전에 전체 인덱스를 한 번 만들어 주세요."),
                InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not refresh a file-index subtree.");
            ShowStatus(
                L("Loc.Files.PartialRefresh.Failed", "부분 새로 고침 실패"),
                UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception),
                InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
            IsBusy = false;
        }

        if (completionNotice is not null)
        {
            _scanCompletionNotifier.Notify(completionNotice);
        }

        if (refreshResults)
        {
            await StartLoadAsync(refreshStatus: false, debounce: false).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ProgressMessage = L("Loc.Common.Cancelling", "취소하는 중…");
        _scanCancellation?.Cancel();
        if (_operationCoordinator.CurrentKind is ApplicationOperationKind.IndexWrite or ApplicationOperationKind.IndexLoad)
            _operationCoordinator.Cancel();
    }

    [RelayCommand]
    private void ToggleSortDirection() =>
        SortDirection = SortDirection == EntrySortDirection.Ascending
            ? EntrySortDirection.Descending
            : EntrySortDirection.Ascending;

    [RelayCommand]
    private void ClearFilters()
    {
        SelectedKind = KindChoices[0];
        SelectedCreatedDateFilter = DateFilterChoices[0];
        SelectedModifiedDateFilter = DateFilterChoices[0];
        CreatedFromDate = null;
        CreatedToDate = null;
        ModifiedFromDate = null;
        ModifiedToDate = null;
        MinimumSizeStep = 0;
        MaximumSizeStep = UnlimitedSizeStep;
        IsFilterOpen = false;
        ScheduleSearch();
    }

    [RelayCommand]
    private void ClearSearchAndFilters()
    {
        SearchText = string.Empty;
        ClearFilters();
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => StartLoadAsync(
        refreshStatus: false,
        debounce: false,
        reset: false);

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void OpenSelected()
    {
        OpenItem(SelectedItem);
    }

    [RelayCommand]
    private void OpenItem(FileSearchItemViewModel? item)
    {
        if (item is null || item.IsPlaceholder)
        {
            return;
        }

        ExecutePathAction(
            () => _pathLauncher.OpenPath(item.FullPath),
            L("Loc.Files.Action.OpenFailed", "항목 열기 실패"));
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void ShowSelectedInFolder()
    {
        if (SelectedItem is { IsPlaceholder: false })
        {
            ExecutePathAction(
                () => _pathLauncher.ShowInFolder(SelectedItem.FullPath),
                L("Loc.Files.Action.ShowInFolderFailed", "위치 열기 실패"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void CopySelectedPath()
    {
        if (SelectedItem is null || SelectedItem.IsPlaceholder)
        {
            return;
        }

        try
        {
            if (!_clipboardService.TrySetText(SelectedItem.FullPath, out var error))
                throw new InvalidOperationException(error ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다."));
            _snackbarService.Show(
                L("Loc.Files.Action.CopyPathCompleted", "경로 복사 완료"),
                SelectedItem.FullPath,
                ControlAppearance.Secondary,
                null,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not copy {Path} to the clipboard.", SelectedItem.FullPath);
            ShowStatus(
                L("Loc.Files.Action.CopyPathFailed", "경로 복사 실패"),
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void OpenSelectedFolder()
    {
        OpenItemFolder(SelectedItem);
    }

    [RelayCommand]
    private void OpenItemFolder(FileSearchItemViewModel? item)
    {
        if (item is null || item.IsPlaceholder)
        {
            return;
        }

        ExecutePathAction(
            () => _pathLauncher.OpenDirectory(item.FolderPath),
            L("Loc.Files.Action.OpenFolderFailed", "폴더 경로 열기 실패"));
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void CopySelectedFolderPath()
    {
        CopyItemFolderPath(SelectedItem);
    }

    [RelayCommand]
    private void CopyItemFolderPath(FileSearchItemViewModel? item)
    {
        if (item is null || item.IsPlaceholder)
        {
            return;
        }

        CopyText(
            item.FolderPath,
            L("Loc.Files.Action.CopyFolderCompleted", "폴더 경로 복사 완료"));
    }

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private async Task CopySelectionAsync()
    {
        if (IsCopyingSelection || SelectedCount == 0) return;
        using var cancellation = new CancellationTokenSource();
        _copyCancellation = cancellation;
        IsCopyingSelection = true;
        var format = SelectedOutputFormat
            ?? SelectionOutputFormatDefaults.CreateFullPathLines(_localizer);
        var scope = _fileQueryScope;
        var request = _fileQueryRequest;
        var capturedSelection = _selection.Capture();
        var legacyItems = _selectedItems.Where(static item => !item.IsPlaceholder).ToArray();
        var selectedCount = SelectedCount;
        try
        {
            ILoadedProfile? profile = null;
            if (!string.IsNullOrWhiteSpace(format.ProfileId)
                && !_profileCatalog.Current.TryGetProfile(format.ProfileId, out profile))
            {
                throw new InvalidOperationException(F(
                    "Loc.Selection.ProfileNotFound",
                    "출력 포맷에 연결된 프로필을 찾을 수 없습니다: {0}",
                    format.ProfileId));
            }

            async IAsyncEnumerable<SelectionOutputItem> ReadItemsAsync(
                [EnumeratorCancellation] CancellationToken token)
            {
                if (scope is not null && request is not null)
                {
                    // The output session owns its lifetime independently of a
                    // new filter, navigation, or the virtual list's eviction.
                    await using var outputQuery = await _fileSearchService.OpenQueryAsync(scope, request, token)
                        .ConfigureAwait(false);
                    await foreach (var entry in outputQuery.EnumerateSelectionAsync(capturedSelection, token)
                        .WithCancellation(token).ConfigureAwait(false))
                        yield return CreateOutputItem(entry, profile);
                    yield break;
                }

                foreach (var item in legacyItems)
                {
                    token.ThrowIfCancellationRequested();
                    yield return CreateOutputItem(item.Entry, profile);
                }
            }

            var text = await Task.Run(() => _outputFormatter.FormatAsync(format,
                ReadItemsAsync(cancellation.Token), cancellation.Token), cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_clipboardService.TrySetText(text, out var errorMessage))
            {
                throw new InvalidOperationException(
                    errorMessage
                    ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다."));
            }

            _snackbarService.Show(
                L("Loc.Selection.CopyCompleted", "선택 항목 복사 완료"),
                F(
                    "Loc.Selection.CopyCompleted.Message",
                    "{0:N0}개 항목을 '{1}' 형식으로 복사했습니다.",
                    selectedCount,
                    format.DisplayName),
                ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not format selected file-search items.");
            ShowStatus(
                L("Loc.Selection.CopyFailed", "선택 항목 복사 실패"),
                UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception),
                InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_copyCancellation, cancellation)) _copyCancellation = null;
            IsCopyingSelection = false;
        }
    }

    private SelectionOutputItem CreateOutputItem(IndexedPathEntry entry, ILoadedProfile? profile)
    {
        var folderPath = entry.Kind == IndexedPathKind.Directory ? entry.FullPath : entry.ParentPath;
        IReadOnlyDictionary<string, object?> fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            var profilePath = _profilePathCanonicalizer.Canonicalize(folderPath);
            var mapping = profile.Map(new ProfilePathCandidate(profilePath));
            if (mapping.Status != ProfileMapStatus.Success || mapping.Item is null)
            {
                var issue = mapping.Issues.FirstOrDefault();
                var reason = issue is null
                    ? L("Loc.Selection.ProfileNoMatch", "프로필 규칙과 일치하지 않습니다.")
                    : ProfileDiagnosticLocalizer.Translate(_localizer, issue);
                throw new InvalidOperationException(F("Loc.Selection.StructuredValueFailed",
                    "'{0}'에서 구조화 값을 가져올 수 없습니다. {1}", folderPath, reason));
            }
            fields = mapping.Item.Values;
        }
        return new SelectionOutputItem(entry.FullPath, folderPath, entry.Name, fields);
    }

    public void SetSelection(IEnumerable<FileSearchItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (UsesQuerySelection) return;
        _selectedItems = items.Where(static item => !item.IsPlaceholder).Distinct().ToArray();
        SelectedCount = _selectedItems.Count;
        CopySelectionCommand.NotifyCanExecuteChanged();
    }

    public void ApplySort(EntrySortField field, EntrySortDirection direction)
    {
        if (field == EntrySortField.Kind) field = EntrySortField.FileType;
        SelectedSort = SortChoices.First(choice => choice.Value == field);
        SortDirection = direction;
    }

    private bool CanRefreshIndex() => !IsBusy && HasRoot;

    private bool CanBrowseRefreshScope() => !IsBusy && HasRoot && HasIndex;

    private bool CanRefreshScope() =>
        !IsBusy && HasRoot && HasIndex && !string.IsNullOrWhiteSpace(RefreshScopePath);

    private bool CanRefreshItemScope(FileSearchItemViewModel? item)
    {
        if (IsBusy || !HasRoot || !HasIndex || item is null || item.IsPlaceholder)
        {
            return false;
        }

        try
        {
            return !PathsEqual(item.FolderPath, RootPath!);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private bool CanChangeWorkspace() => !IsBusy;

    private bool CanUseDefaultDatabasePath() => !IsBusy && !IsUsingDefaultDatabasePath;

    private bool CanCancel() => IsBusy;

    private bool CanLoadMore() => HasMore && !IsSearching && !IsBusy;

    private bool CanUseSelected() => SelectedItem is { IsPlaceholder: false }
        && (!UsesQuerySelection || (SelectedCount == 1 && _selection.Contains(SelectedItem.FullPath)));

    private bool CanCopySelection() => SelectedCount > 0 && !IsSearching && !IsCopyingSelection;

    private void RefreshOutputFormats()
    {
        var selectedId = SelectedOutputFormat?.Id;
        var loadedProfiles = _profileCatalog.Current.Profiles
            .Select(static profile => profile.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OutputFormats = _outputFormatStore.GetAll()
            .Where(format => string.IsNullOrWhiteSpace(format.ProfileId)
                || loadedProfiles.Contains(format.ProfileId))
            .ToArray();
        SelectedOutputFormat = OutputFormats.FirstOrDefault(format =>
                string.Equals(format.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? OutputFormats.FirstOrDefault(format => format.IsBuiltIn)
            ?? OutputFormats.FirstOrDefault();
    }

    private void OnOutputFormatsChanged(object? sender, EventArgs eventArgs) =>
        Dispatch(RefreshOutputFormats);

    private void OnProfilesChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs) =>
        Dispatch(RefreshOutputFormats);

    private void Dispatch(Action action) => _uiScheduler.Post(() => { if (!_disposed) action(); });

    private void CopyText(string text, string title)
    {
        if (_clipboardService.TrySetText(text, out var errorMessage))
        {
            _snackbarService.Show(
                title,
                text,
                ControlAppearance.Secondary,
                null,
                TimeSpan.FromSeconds(3));
            return;
        }

        ShowStatus(
            title,
            errorMessage ?? L("Loc.Common.ClipboardFailed", "클립보드에 복사하지 못했습니다."),
            InfoBarSeverity.Error);
    }

    private Task InitializeAsync() => StartLoadAsync(refreshStatus: true, debounce: false);

    private async Task<bool> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        if (!HasRoot)
        {
            HasIndex = false;
            IndexStatusTitle = L("Loc.Files.Status.ChooseRoot", "검색 위치를 선택하세요");
            IndexStatusDetail = L(
                "Loc.Files.Status.ChooseRoot.Detail",
                "선택한 위치만 색인하며 원본 파일은 변경하지 않습니다.");
            return true;
        }

        var generation = Volatile.Read(ref _scopeVersion);
        var scope = ValidateScope(requireExistingRoot: false);
        var status = await Task.Run(() => _fileSearchService.GetStatusAsync(scope, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        if (generation != Volatile.Read(ref _scopeVersion)
            || !ScopeStillCurrent(scope))
        {
            return false;
        }

        HasIndex = status.Availability == IndexRootAvailability.Available;
        if (!HasIndex) ClearSearchResults();
        HasPendingScopes = status.HasPendingScopes;
        switch (status.Availability)
        {
            case IndexRootAvailability.DatabaseMissing:
            case IndexRootAvailability.RootNotIndexed:
                IndexStatusTitle = L(
                    "Loc.Files.Status.NotIndexed",
                    "이 위치는 아직 색인되지 않았습니다");
                IndexStatusDetail = L(
                    "Loc.Files.Status.NotIndexed.Detail",
                    "인덱스 만들기를 실행하면 파일명과 메타데이터를 빠르게 검색할 수 있습니다.");
                break;
            case IndexRootAvailability.Available:
                IndexStatusTitle = status.HasPendingScopes
                    ? L("Loc.Files.Status.PartialIndex", "일부 범위 색인됨")
                    : L("Loc.Files.Status.Ready", "검색 준비 완료");
                var published = status.LastPublishedUtc?.LocalDateTime.ToString("g")
                    ?? L("Loc.Common.TimeUnavailable", "시간 정보 없음");
                IndexStatusDetail = status.LastScopePath is { Length: > 0 } lastScope
                    && !PathsEqual(status.RootPath, lastScope)
                        ? F(
                            "Loc.Files.Status.PartialRefreshDetail",
                            "{0:N0}개 항목 · '{1}' 부분 새로 고침 · {2}",
                            status.EntryCount,
                            lastScope,
                            published)
                        : F(
                            "Loc.Files.Status.IndexDetail",
                            "{0:N0}개 항목 · 마지막 새로 고침 {1}",
                            status.EntryCount,
                            published);
                break;
        }

        RefreshIndexCommand.NotifyCanExecuteChanged();
        BrowseRefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshItemScopeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSetupStateVisible));
        return true;
    }

    private void ScheduleSearch(bool preserveSelection = false)
    {
        if (_disposed || !CanSearch)
        {
            return;
        }

        _ = StartLoadAsync(refreshStatus: false, debounce: true, preserveSelection: preserveSelection);
    }

    private void InvalidateSearch()
    {
        _queryCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        Interlocked.Increment(ref _queryVersion);
        IsSearching = false;
    }

    private void InvalidateScope()
    {
        Interlocked.Increment(ref _scopeVersion);
        InvalidateSearch();
        _loadedSearchSnapshot = null;
        HasIndex = false;
        HasMore = false;
        HasPendingScopes = false;
        IndexStatusTitle = L("Loc.Files.Status.Checking", "인덱스 상태 확인 중");
        IndexStatusDetail = L(
            "Loc.Files.Status.Checking.Detail",
            "선택한 위치의 기존 인덱스를 확인하고 있습니다.");
    }

    private Task StartLoadAsync(
        bool refreshStatus,
        bool debounce,
        bool reset = true,
        bool preserveSelection = false)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _queryCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        if (!preserveSelection)
        {
            _selection.Clear();
            _selectedItems = [];
            SelectedItem = null;
            if (UsesQuerySelection) UpdateQuerySelection(); else SelectedCount = 0;
        }
        IsSearching = true;
        var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        var version = Interlocked.Increment(ref _queryVersion);
        var task = RunLoadSessionAsync(
            refreshStatus,
            debounce,
            reset,
            preserveSelection,
            version,
            cancellation);
        _activeLoadTask = task;
        return task;
    }

    private async Task RunLoadSessionAsync(
        bool refreshStatus,
        bool debounce,
        bool reset,
        bool preserveSelection,
        long version,
        CancellationTokenSource cancellation)
    {
        try
        {
            // Keep ownership fields initialized even when there is no index to load.
            await Task.Yield();
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token)
                    .ConfigureAwait(true);
            }

            var statusApplied = !refreshStatus
                || await RefreshStatusAsync(cancellation.Token).ConfigureAwait(true);
            if (statusApplied && HasIndex)
            {
                await SearchPagesAsync(reset, preserveSelection, version, cancellation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer query or an index refresh superseded this load session.
        }
        catch (Exception exception)
        {
            if (version != Volatile.Read(ref _queryVersion) || cancellation.IsCancellationRequested) return;
            ClearSearchResults();
            _logger.LogWarning(exception, "Could not load the file index.");
            ShowStatus(
                refreshStatus
                    ? L("Loc.Files.Status.CheckFailed", "인덱스 상태 확인 실패")
                    : L("Loc.Files.Search.Failed", "검색 실패"),
                UserFacingExceptionLocalizer.TranslateOperationFailure(
                    _localizer,
                    exception),
                refreshStatus ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation))
            {
                _queryCancellation = null;
                _activeLoadTask = null;
                IsSearching = false;
            }

            cancellation.Dispose();
        }
    }

    private async Task CancelActiveLoadAsync()
    {
        var cancellation = _queryCancellation;
        var task = _activeLoadTask;
        InvalidateSearch();
        if (cancellation is null || task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task SearchPagesAsync(bool reset, bool preserveSelection, long version, CancellationToken cancellationToken)
    {
        if (!CanSearch) return;
        IsSearching = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var scope = ValidateScope(requireExistingRoot: false);
                var status = await Task.Run(() => _fileSearchService.GetStatusAsync(scope, cancellationToken), cancellationToken);
                var request = BuildSearchRequest(scope.RootPath, 0) with { ExpectedScanId = status.LastScanId };
                IResultQuerySession<IndexedPathEntry> query;
                try
                {
                    query = await Task.Run(() => _fileSearchService.OpenQueryAsync(scope, request, cancellationToken), cancellationToken);
                }
                catch (IndexSnapshotChangedException) when (attempt == 0)
                {
                    ProgressMessage = L("Loc.Files.Progress.IndexChangedRetry", "인덱스가 갱신되어 다시 불러옵니다…");
                    continue;
                }
                try
                {
                    var first = await Task.Run(() => query.ReadRangeAsync(0, 500, cancellationToken), cancellationToken);
                    if (version != Volatile.Read(ref _queryVersion)) { await query.DisposeAsync(); return; }
                    var retainedSelection = preserveSelection
                        && _fileQueryRequest is { } oldRequest && _fileQueryScope == scope
                        && oldRequest.ExpectedScanId == request.ExpectedScanId
                        && SameResultFilters(oldRequest, request)
                            ? _selection.Capture() : new QuerySelection();
                    var retainedItem = retainedSelection.Count(long.MaxValue) > 0 ? SelectedItem : null;
                    var previousTotalCount = TotalCount;
                    ReleaseFileQuery();
                    _fileQuery = query;
                    _fileQueryScope = scope;
                    _fileQueryRequest = request;
                    RestoreSelection(retainedSelection);
                    SelectedItem = retainedItem;
                    _selectionAnchor = -1;
                    var collection = new VirtualResultCollection<FileSearchItemViewModel>(
                        async (offset, limit, token) =>
                        {
                            var page = await query.ReadRangeAsync(offset, limit, token).ConfigureAwait(false);
                            return new ResultPage<FileSearchItemViewModel>(page.Items.Select(CreateFileRow).ToArray(), offset, page.HasMore);
                        },
                        _ => new FileSearchItemViewModel(new IndexedPathEntry(string.Empty,
                            L("Loc.Common.Loading", "불러오는 중…"), string.Empty, IndexedPathKind.File,
                            null, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false), _localizer) { IsPlaceholder = true },
                        Dispatch,
                        exception => { if (ReferenceEquals(query, _fileQuery)) OnFilePageFailed(exception); });
                    collection.PageLoaded += (_, _) => UpdateQuerySelection();
                    collection.Seed(new ResultPage<FileSearchItemViewModel>(
                        first.Items.Select(CreateFileRow).ToArray(), 0, first.HasMore));
                    Items = collection;
                    TotalCount = retainedItem is not null || retainedSelection.AllMatching
                        ? previousTotalCount : first.Items.Count;
                    IsResultCountPending = true;
                    UpdateQuerySelection();
                    HasMore = false;
                    HasSearched = true;
                    _loadedSearchSnapshot = status;
                    OnPropertyChanged(nameof(ResultSourceDescription));
                    OnPropertyChanged(nameof(UsesQuerySelection));
                    OnPropertyChanged(nameof(ResultSummary));
                    var total = await Task.Run(() => query.CountAsync(cancellationToken), cancellationToken);
                    if (version != Volatile.Read(ref _queryVersion)) return;
                    collection.SetCount(total);
                    TotalCount = total;
                    IsResultCountPending = false;
                    UpdateQuerySelection();
                    ProgressMessage = F("Loc.Files.Results.Summary", "{0:N0}개 결과 · {1:N0}개 표시", total, collection.CachedItemCount);
                    OnPropertyChanged(nameof(ResultSummary));
                    OnPropertyChanged(nameof(IsNoResultsVisible));
                    break;
                }
                catch (IndexSnapshotChangedException) when (attempt == 0)
                {
                    if (ReferenceEquals(query, _fileQuery)) ClearSearchResults();
                    await query.DisposeAsync();
                    ProgressMessage = L("Loc.Files.Progress.IndexChangedRetry", "인덱스가 갱신되어 다시 불러옵니다…");
                }
                catch
                {
                    if (ReferenceEquals(query, _fileQuery)) ClearSearchResults();
                    await query.DisposeAsync();
                    throw;
                }
            }
        }
        finally { if (version == Volatile.Read(ref _queryVersion)) IsSearching = false; }
    }

    private FileSearchItemViewModel CreateFileRow(IndexedPathEntry entry) =>
        new(entry, _localizer);

    private void ReleaseFileQuery()
    {
        _selectionCancellation?.Cancel();
        if (Items is IDisposable disposable) disposable.Dispose();
        if (_fileQuery is not null) _ = _fileQuery.DisposeAsync();
        _fileQuery = null;
        _fileQueryScope = null;
        _fileQueryRequest = null;
        Items = new ObservableCollection<FileSearchItemViewModel>();
        _selection.Clear();
        _selectionAnchor = -1;
        _selectedItems = [];
        SelectedItem = null;
        SelectedCount = 0;
        IsResultCountPending = false;
        OnPropertyChanged(nameof(UsesQuerySelection));
        OnPropertyChanged(nameof(ResultSourceDescription));
    }

    private static bool SameResultFilters(EntrySearchRequest left, EntrySearchRequest right) =>
        left with { SortBy = right.SortBy, SortDirection = right.SortDirection, Limit = right.Limit,
            Offset = 0, AfterCursor = null, IncludeTotalCount = right.IncludeTotalCount } == right;

    private void RestoreSelection(QuerySelection captured)
    {
        _selection.Clear();
        if (captured.AllMatching) _selection.SelectAll();
        foreach (var key in captured.Keys) _selection.Set(key, !captured.AllMatching);
    }

    private void OnFilePageFailed(Exception exception)
    {
        if (_disposed) return;
        if (exception is IndexSnapshotChangedException)
        {
            _ = StartLoadAsync(refreshStatus: true, debounce: false);
            return;
        }
        ClearSearchResults();
        ShowStatus(L("Loc.Files.Search.Failed", "검색 실패"),
            UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception), InfoBarSeverity.Error);
    }

    public bool UsesQuerySelection => _fileQuery is not null;

    [RelayCommand]
    private void SelectAll()
    {
        if (_fileQuery is null || IsSearching) return;
        _selectionCancellation?.Cancel();
        _selection.SelectAll(); UpdateQuerySelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selectionCancellation?.Cancel();
        _selection.Clear(); SelectedItem = null; UpdateQuerySelection();
    }

    [RelayCommand]
    private void InvertSelection()
    {
        if (_fileQuery is null || IsSearching) return;
        _selectionCancellation?.Cancel();
        _selection.Invert(); UpdateQuerySelection();
    }

    public async Task SelectRowAsync(object value, bool additive, bool range)
    {
        if (value is not FileSearchItemViewModel row || row.IsPlaceholder || IsSearching) return;
        if (_fileQuery is null) return;
        var query = _fileQuery;
        var index = Items.IndexOf(row);
        _selectionCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _selectionCancellation = cancellation;
        var version = Volatile.Read(ref _queryVersion);
        var captured = additive ? _selection.Capture() : new QuerySelection();
        try
        {
            if (range && _selectionAnchor >= 0 && index >= 0)
            {
                var start = Math.Min(index, _selectionAnchor);
                var end = Math.Max(index, _selectionAnchor);
                for (var offset = start; offset <= end; offset += 500)
                {
                    var page = await Task.Run(() => query.ReadRangeAsync(offset,
                        Math.Min(500, end - offset + 1), cancellation.Token), cancellation.Token).ConfigureAwait(true);
                    if (!ReferenceEquals(query, _fileQuery) || version != Volatile.Read(ref _queryVersion)) return;
                    foreach (var entry in page.Items) captured.Set(entry.FullPath, true);
                }
            }
            else
            {
                captured.Set(row.FullPath, additive ? !_selection.Contains(row.FullPath) : true);
                _selectionAnchor = index;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            RestoreSelection(captured);
            SelectedItem = row;
            UpdateQuerySelection();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(query, _fileQuery)) OnFilePageFailed(exception);
        }
        finally { if (ReferenceEquals(_selectionCancellation, cancellation)) _selectionCancellation = null; }
    }

    private void UpdateQuerySelection()
    {
        if (!UsesQuerySelection) return;
        if (Items is VirtualResultCollection<FileSearchItemViewModel> list)
            foreach (var row in list.LoadedItems) row.IsSelected = _selection.Contains(row.FullPath);
        SelectedCount = checked((int)_selection.Count(TotalCount));
        OnPropertyChanged(nameof(HasSelection));
        CopySelectionCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
        ShowSelectedInFolderCommand.NotifyCanExecuteChanged();
        CopySelectedPathCommand.NotifyCanExecuteChanged();
        OpenSelectedFolderCommand.NotifyCanExecuteChanged();
        CopySelectedFolderPathCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void ClearSearchResults()
    {
        ReleaseFileQuery();
        TotalCount = 0;
        HasMore = false;
        HasPendingScopes = false;
        HasSearched = false;
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(IsNoResultsVisible));
    }

    private EntrySearchRequest BuildSearchRequest(string rootPath, int offset)
    {
        var created = ResolveDateRange(
            SelectedCreatedDateFilter.Value,
            CreatedFromDate,
            CreatedToDate);
        var modified = ResolveDateRange(
            SelectedModifiedDateFilter.Value,
            ModifiedFromDate,
            ModifiedToDate);
        return new EntrySearchRequest(rootPath)
        {
            Keyword = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Kind = SelectedKind.Value,
            MinSizeBytes = MinimumSizeStep <= 0 ? null : SizeStepToBytes(MinimumSizeStep),
            MaxSizeBytes = MaximumSizeStep >= UnlimitedSizeStep ? null : SizeStepToBytes(MaximumSizeStep),
            CreatedFromUtc = created.From,
            CreatedBeforeUtc = created.Before,
            ModifiedFromUtc = modified.From,
            ModifiedBeforeUtc = modified.Before,
            SortBy = SelectedSort.Value,
            SortDirection = SortDirection,
            Limit = Math.Clamp(_indexingSettings.Current.SearchPageSize, 1, 1000),
            Offset = offset,
        };
    }

    private FileSearchScope ValidateScope(bool requireExistingRoot = true)
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException(L(
                "Loc.Files.Error.SelectRoot",
                "검색할 폴더나 드라이브를 선택하세요."));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        if (requireExistingRoot && !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(F(
                "Loc.Files.Error.RootNotFound",
                "검색 위치를 찾을 수 없습니다: {0}",
                root));
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException(L(
                "Loc.Files.Error.DatabaseMissing",
                "인덱스 저장 위치를 확인할 수 없습니다."));
        }

        return new FileSearchScope(root, Path.GetFullPath(DatabasePath));
    }

    private string ValidateRefreshScopePath(FileSearchScope scope, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(L(
                "Loc.Files.PartialRefresh.Error.Required",
                "새로 고칠 하위 폴더를 선택하거나 입력하세요."));
        }

        var candidate = path.Trim();
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException(L(
                "Loc.Files.PartialRefresh.Error.Absolute",
                "드라이브 또는 UNC 루트를 포함한 전체 폴더 경로를 입력하세요."));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (PathsEqual(scope.RootPath, normalized))
        {
            throw new ArgumentException(L(
                "Loc.Files.PartialRefresh.Error.Root",
                "검색 위치 전체가 아닌 그 아래의 하위 폴더를 선택하세요. 전체 위치는 '인덱스 새로 고침'을 사용합니다."));
        }

        var relativePath = Path.GetRelativePath(scope.RootPath, normalized);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(L(
                "Loc.Files.PartialRefresh.Error.OutsideRoot",
                "부분 새로 고침 폴더는 현재 검색 위치 안에 있어야 합니다."));
        }

        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(F(
                "Loc.Files.PartialRefresh.Error.NotFound",
                "새로 고칠 폴더를 찾을 수 없습니다: {0}",
                normalized));
        }

        return normalized;
    }

    private bool ScopeStillCurrent(FileSearchScope scope)
    {
        var root = RootPath;
        var database = DatabasePath;
        return !string.IsNullOrWhiteSpace(root)
            && !string.IsNullOrWhiteSpace(database)
            && PathsEqual(scope.RootPath, root)
            && PathsEqual(scope.DatabasePath, database);
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            comparison);
    }

    private void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(L(
                "Loc.Files.Error.DatabaseParentMissing",
                "인덱스 DB의 상위 폴더를 확인할 수 없습니다."));
        }

        Directory.CreateDirectory(directory);
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer.Format(key, koreanFallback, arguments);

    private static (DateTimeOffset? From, DateTimeOffset? Before) ResolveDateRange(
        DateFilterPreset preset,
        DateTime? customFrom,
        DateTime? customTo)
    {
        var today = DateTime.Today;
        return preset switch
        {
            DateFilterPreset.Any => (null, null),
            DateFilterPreset.Today => (ToUtc(today), ToUtc(today.AddDays(1))),
            DateFilterPreset.LastSevenDays => (ToUtc(today.AddDays(-6)), ToUtc(today.AddDays(1))),
            DateFilterPreset.LastThirtyDays => (ToUtc(today.AddDays(-29)), ToUtc(today.AddDays(1))),
            DateFilterPreset.Custom => (
                customFrom is null ? null : ToUtc(customFrom.Value.Date),
                customTo is null ? null : ToUtc(customTo.Value.Date.AddDays(1))),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown date filter preset."),
        };
    }

    private static DateTimeOffset ToUtc(DateTime localDate) =>
        new DateTimeOffset(DateTime.SpecifyKind(localDate, DateTimeKind.Local)).ToUniversalTime();

    private static long? SizeStepToBytes(double rawStep)
    {
        var step = Math.Clamp((int)Math.Round(rawStep), 0, UnlimitedSizeStep);
        if (step <= 0 || step >= UnlimitedSizeStep)
        {
            return null;
        }

        // 1 KB, 10 KB, 100 KB, 1 MB ... 1 TB.
        var group = (step - 1) / 3 + 1;
        var multiplier = (step - 1) % 3 switch
        {
            0 => 1L,
            1 => 10L,
            _ => 100L,
        };
        return checked(multiplier * (long)Math.Pow(1024, group));
    }

    private void ScheduleCustomDateSearch(bool isCustom)
    {
        if (isCustom)
        {
            ScheduleSearch();
        }
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs eventArgs)
    {
        void Apply()
        {
            var databasePath = _databasePathResolver.ResolveFileSearchDatabasePath(
                eventArgs.Current);
            var rootChanged = !string.Equals(RootPath, eventArgs.Current.RootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase);
            if (rootChanged)
            {
                _scanCancellation?.Cancel();
                InvalidateScope();
            }

            RootPath = eventArgs.Current.RootPath;
            DatabasePath = databasePath;
            IsUsingDefaultDatabasePath = string.IsNullOrWhiteSpace(
                eventArgs.Current.DatabasePath);
            if (rootChanged)
            {
                ClearSearchResults();
                _ = InitializeAsync();
            }
        }

        Dispatch(Apply);
    }

    private void ExecutePathAction(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A file-system shell action failed.");
            ShowStatus(title, exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspaceContext.Changed -= OnWorkspaceChanged;
        _outputFormatStore.Changed -= OnOutputFormatsChanged;
        _profileCatalog.Changed -= OnProfilesChanged;
        InvalidateSearch();
        _scanCancellation?.Cancel();
        _copyCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        ReleaseFileQuery();
    }
}
