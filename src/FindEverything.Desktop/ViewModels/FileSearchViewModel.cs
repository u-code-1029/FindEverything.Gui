using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Desktop.Services;
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
    private readonly IDesktopPickerService _pickerService;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly IScanCompletionNotifier _scanCompletionNotifier;
    private readonly ISnackbarService _snackbarService;
    private readonly ILogger<FileSearchViewModel> _logger;
    private CancellationTokenSource? _queryCancellation;
    private long _queryVersion;
    private long _scopeVersion;
    private bool _coercingSizeRange;
    private bool _disposed;

    public FileSearchViewModel(
        IFileSearchService fileSearchService,
        IValidatedSettingsState<IndexingOptions> indexingSettings,
        IWorkspaceContext workspaceContext,
        IDesktopPickerService pickerService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        IScanCompletionNotifier scanCompletionNotifier,
        ISnackbarService snackbarService,
        ILogger<FileSearchViewModel> logger)
    {
        _fileSearchService = fileSearchService;
        _indexingSettings = indexingSettings;
        _workspaceContext = workspaceContext;
        _pickerService = pickerService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _scanCompletionNotifier = scanCompletionNotifier;
        _snackbarService = snackbarService;
        _logger = logger;

        KindChoices =
        [
            new(null, "전체"),
            new(IndexedPathKind.File, "파일"),
            new(IndexedPathKind.Directory, "폴더"),
        ];
        SortChoices =
        [
            new(EntrySortField.Name, "이름"),
            new(EntrySortField.Path, "경로"),
            new(EntrySortField.Kind, "종류"),
            new(EntrySortField.Size, "크기"),
            new(EntrySortField.Modified, "수정일"),
            new(EntrySortField.Created, "생성일"),
        ];
        DateFilterChoices =
        [
            new(DateFilterPreset.Any, "전체 기간"),
            new(DateFilterPreset.Today, "오늘"),
            new(DateFilterPreset.LastSevenDays, "최근 7일"),
            new(DateFilterPreset.LastThirtyDays, "최근 30일"),
            new(DateFilterPreset.Custom, "직접 지정"),
        ];

        SelectedKind = KindChoices[0];
        SelectedSort = SortChoices[0];
        SelectedCreatedDateFilter = DateFilterChoices[0];
        SelectedModifiedDateFilter = DateFilterChoices[0];
        RootPath = workspaceContext.Current.RootPath;
        DatabasePath = workspaceContext.Current.DatabasePath;
        _workspaceContext.Changed += OnWorkspaceChanged;
        _ = InitializeAsync();
    }

    public ObservableCollection<FileSearchItemViewModel> Items { get; } = [];

    public IReadOnlyList<EntryKindChoice> KindChoices { get; }

    public IReadOnlyList<EntrySortChoice> SortChoices { get; }

    public IReadOnlyList<DateFilterChoice> DateFilterChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    [NotifyPropertyChangedFor(nameof(HasNoRoot))]
    [NotifyPropertyChangedFor(nameof(IsSetupStateVisible))]
    [NotifyCanExecuteChangedFor(nameof(RefreshIndexCommand))]
    private string? _rootPath;

    [ObservableProperty]
    private string _databasePath = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(IsSetupStateVisible))]
    [NotifyPropertyChangedFor(nameof(IsNoResultsVisible))]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
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

    public bool CanSearch => HasIndex && !IsBusy;

    public bool IsSetupStateVisible => !HasRoot || !HasIndex;

    public bool IsNoResultsVisible => HasIndex && HasSearched && !IsSearching && Items.Count == 0;

    public string MinimumSizeText => MinimumSizeStep <= 0
        ? "제한 없음"
        : FileSizeFormatter.Format(SizeStepToBytes(MinimumSizeStep) ?? 0);

    public string MaximumSizeText => MaximumSizeStep >= UnlimitedSizeStep
        ? "제한 없음"
        : FileSizeFormatter.Format(SizeStepToBytes(MaximumSizeStep) ?? 0);

    public string ResultSummary => HasSearched
        ? $"{TotalCount:N0}개 결과 · {Items.Count:N0}개 표시"
        : "검색 준비";

    public string SortDirectionText => SortDirection == EntrySortDirection.Ascending
        ? "오름차순"
        : "내림차순";

    public bool IsCreatedCustomRange => SelectedCreatedDateFilter.Value == DateFilterPreset.Custom;

    public bool IsModifiedCustomRange => SelectedModifiedDateFilter.Value == DateFilterPreset.Custom;

    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnSelectedKindChanged(EntryKindChoice value) => ScheduleSearch();

    partial void OnSelectedSortChanged(EntrySortChoice value) => ScheduleSearch();

    partial void OnSortDirectionChanged(EntrySortDirection value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        ScheduleSearch();
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
    }

    partial void OnIsBusyChanged(bool value)
    {
        BrowseRootCommand.NotifyCanExecuteChanged();
        BrowseDatabaseCommand.NotifyCanExecuteChanged();
        RefreshIndexCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSearch));
    }

    partial void OnIsSearchingChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanChangeWorkspace))]
    private async Task BrowseRootAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            ShowStatus("다른 작업 진행 중", "현재 작업이 끝난 뒤 검색 위치를 변경하세요.", InfoBarSeverity.Warning);
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
            var current = _workspaceContext.Current;
            await _workspaceContext.SaveAsync(
                current with { RootPath = normalized },
                CancellationToken.None).ConfigureAwait(true);
            InvalidateScope();
            RootPath = normalized;
            Items.Clear();
            TotalCount = 0;
            HasSearched = false;
            var statusApplied = await RefreshStatusAsync().ConfigureAwait(true);
            if (statusApplied && HasIndex)
            {
                await SearchAsync(reset: true, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not change the file-search root.");
            ShowStatus("검색 위치 변경 실패", exception.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeWorkspace))]
    private async Task BrowseDatabaseAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            ShowStatus("다른 작업 진행 중", "현재 작업이 끝난 뒤 인덱스 위치를 변경하세요.", InfoBarSeverity.Warning);
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
            if (HasRoot && IsWithin(normalized, RootPath!))
            {
                throw DatabaseInsideRootException();
            }

            var current = _workspaceContext.Current;
            await _workspaceContext.SaveAsync(
                current with { DatabasePath = normalized },
                CancellationToken.None).ConfigureAwait(true);
            InvalidateScope();
            DatabasePath = normalized;
            Items.Clear();
            TotalCount = 0;
            HasSearched = false;
            var statusApplied = await RefreshStatusAsync().ConfigureAwait(true);
            if (statusApplied && HasIndex)
            {
                await SearchAsync(reset: true, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not change the file-search database.");
            ShowStatus("인덱스 위치 변경 실패", exception.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshIndex))]
    private async Task RefreshIndexAsync()
    {
        if (_operationCoordinator.IsRunning)
        {
            ShowStatus("다른 작업 진행 중", "현재 작업이 끝난 뒤 다시 시도하세요.", InfoBarSeverity.Warning);
            return;
        }

        FileSearchScope scope;
        try
        {
            scope = ValidateScope();
            EnsureDatabaseDirectory(scope.DatabasePath);
            var current = _workspaceContext.Current;
            await _workspaceContext.SaveAsync(
                current with { RootPath = scope.RootPath, DatabasePath = scope.DatabasePath },
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowStatus("검색 위치 확인 필요", exception.Message, InfoBarSeverity.Error);
            return;
        }

        InvalidateSearch();
        IsBusy = true;
        ProgressMessage = "인덱스를 준비하고 있습니다…";
        var refreshResults = false;
        ScanCompletionNotice? completionNotice = null;
        var progress = new Progress<IndexScanProgress>(value =>
        {
            ProgressMessage = $"{value.Entries:N0}개 항목 · {value.Directories:N0}개 폴더";
        });
        try
        {
            IndexScanReport? report = null;
            await _operationCoordinator.RunAsync(async cancellationToken =>
            {
                report = await _fileSearchService.ScanAsync(scope, progress, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(true);

            if (report is null)
            {
                throw new InvalidOperationException("인덱싱 결과를 받지 못했습니다.");
            }

            var incomplete = report.Status != IndexScanStatus.Completed || report.Errors.Count > 0;
            ShowStatus(
                incomplete ? "일부 항목 색인 완료" : "인덱스 새로 고침 완료",
                $"{report.Progress.Entries:N0}개 항목을 확인했습니다. 오류 {report.Errors.Count:N0}개",
                incomplete ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            _snackbarService.Show(
                "인덱스 새로 고침 완료",
                $"{report.Progress.Entries:N0}개 항목을 검색할 수 있습니다.",
                incomplete ? ControlAppearance.Caution : ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(4));
            refreshResults = await RefreshStatusAsync().ConfigureAwait(true) && HasIndex;
            completionNotice = new ScanCompletionNotice(
                incomplete ? "일부 항목 색인 완료" : "인덱스 새로 고침 완료",
                $"{report.Progress.Entries:N0}개 항목을 검색할 수 있습니다."
                    + (incomplete ? $" 오류 {report.Errors.Count:N0}개를 확인하세요." : string.Empty),
                incomplete);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("인덱싱 취소", "기존에 게시된 인덱스는 그대로 유지됩니다.", InfoBarSeverity.Warning);
            ProgressMessage = "취소됨";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not refresh the file index.");
            ShowStatus("인덱싱 실패", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }

        if (completionNotice is not null)
        {
            _scanCompletionNotifier.Notify(completionNotice);
        }

        if (refreshResults)
        {
            await SearchAsync(reset: true, CancellationToken.None).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ProgressMessage = "취소하는 중…";
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
    private Task LoadMoreAsync() => SearchAsync(
        reset: false,
        CancellationToken.None,
        Volatile.Read(ref _queryVersion));

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void OpenSelected()
    {
        if (SelectedItem is not null)
        {
            ExecutePathAction(() => _pathLauncher.OpenPath(SelectedItem.FullPath), "항목 열기 실패");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void ShowSelectedInFolder()
    {
        if (SelectedItem is not null)
        {
            ExecutePathAction(() => _pathLauncher.ShowInFolder(SelectedItem.FullPath), "위치 열기 실패");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void CopySelectedPath()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(SelectedItem.FullPath);
            _snackbarService.Show(
                "경로 복사 완료",
                SelectedItem.FullPath,
                ControlAppearance.Secondary,
                null,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not copy {Path} to the clipboard.", SelectedItem.FullPath);
            ShowStatus("경로 복사 실패", exception.Message, InfoBarSeverity.Error);
        }
    }

    public void ApplySort(EntrySortField field, EntrySortDirection direction)
    {
        SelectedSort = SortChoices.First(choice => choice.Value == field);
        SortDirection = direction;
    }

    private bool CanRefreshIndex() => !IsBusy && HasRoot;

    private bool CanChangeWorkspace() => !IsBusy;

    private bool CanCancel() => IsBusy;

    private bool CanLoadMore() => HasMore && !IsSearching && !IsBusy;

    private bool CanUseSelected() => SelectedItem is not null;

    private async Task InitializeAsync()
    {
        try
        {
            var statusApplied = await RefreshStatusAsync().ConfigureAwait(true);
            if (statusApplied && HasIndex)
            {
                await SearchAsync(reset: true, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not initialize the file search page.");
            ShowStatus("인덱스 상태 확인 실패", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task<bool> RefreshStatusAsync()
    {
        if (!HasRoot)
        {
            HasIndex = false;
            IndexStatusTitle = "검색 위치를 선택하세요";
            IndexStatusDetail = "선택한 위치만 색인하며 원본 파일은 변경하지 않습니다.";
            return true;
        }

        var generation = Volatile.Read(ref _scopeVersion);
        var scope = ValidateScope(validateDatabaseOutsideRoot: false);
        var status = await _fileSearchService.GetStatusAsync(scope).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _scopeVersion)
            || !ScopeStillCurrent(scope))
        {
            return false;
        }

        HasIndex = status.Availability == IndexRootAvailability.Available;
        HasPendingScopes = status.HasPendingScopes;
        switch (status.Availability)
        {
            case IndexRootAvailability.DatabaseMissing:
            case IndexRootAvailability.RootNotIndexed:
                IndexStatusTitle = "이 위치는 아직 색인되지 않았습니다";
                IndexStatusDetail = "인덱스 만들기를 실행하면 파일명과 메타데이터를 빠르게 검색할 수 있습니다.";
                break;
            case IndexRootAvailability.Available:
                IndexStatusTitle = status.HasPendingScopes ? "일부 범위 색인됨" : "검색 준비 완료";
                var published = status.LastPublishedUtc?.LocalDateTime.ToString("g") ?? "시간 정보 없음";
                IndexStatusDetail = $"{status.EntryCount:N0}개 항목 · 마지막 새로 고침 {published}";
                break;
        }

        RefreshIndexCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSetupStateVisible));
        return true;
    }

    private void ScheduleSearch()
    {
        if (_disposed || !CanSearch)
        {
            return;
        }

        InvalidateSearch();
        _queryCancellation = new CancellationTokenSource();
        var version = Volatile.Read(ref _queryVersion);
        _ = DebounceAndSearchAsync(version, _queryCancellation.Token);
    }

    private void InvalidateSearch()
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
        Interlocked.Increment(ref _queryVersion);
        IsSearching = false;
    }

    private void InvalidateScope()
    {
        Interlocked.Increment(ref _scopeVersion);
        InvalidateSearch();
        HasIndex = false;
        HasMore = false;
        HasPendingScopes = false;
        IndexStatusTitle = "인덱스 상태 확인 중";
        IndexStatusDetail = "선택한 위치의 기존 인덱스를 확인하고 있습니다.";
    }

    private async Task DebounceAndSearchAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(true);
            await SearchAsync(reset: true, cancellationToken, version).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer query superseded this one.
        }
    }

    private async Task SearchAsync(
        bool reset,
        CancellationToken externalCancellation,
        long? expectedVersion = null)
    {
        var version = expectedVersion
            ?? (reset ? Interlocked.Increment(ref _queryVersion) : Volatile.Read(ref _queryVersion));
        if (version != Volatile.Read(ref _queryVersion))
        {
            return;
        }

        if (!CanSearch || (!reset && (!HasMore || IsSearching)))
        {
            return;
        }

        var cancellationToken = externalCancellation;
        IsSearching = true;
        try
        {
            var scope = ValidateScope(validateDatabaseOutsideRoot: false);
            var request = BuildSearchRequest(scope.RootPath, reset ? 0 : Items.Count);
            var result = await _fileSearchService.SearchAsync(scope, request, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _queryVersion))
            {
                return;
            }

            if (reset)
            {
                Items.Clear();
            }

            foreach (var entry in result.Entries)
            {
                Items.Add(new FileSearchItemViewModel(entry));
            }

            TotalCount = result.TotalCount;
            HasMore = result.HasMore;
            HasPendingScopes = result.HasPendingScopes;
            HasSearched = true;
            if (IsStatusOpen
                && StatusSeverity == InfoBarSeverity.Error
                && string.Equals(StatusTitle, "검색 실패", StringComparison.Ordinal))
            {
                IsStatusOpen = false;
            }

            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(IsNoResultsVisible));
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "File index search failed.");
            ShowStatus("검색 실패", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (version == Volatile.Read(ref _queryVersion))
            {
                IsSearching = false;
            }
        }
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

    private FileSearchScope ValidateScope(bool validateDatabaseOutsideRoot = true)
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException("검색할 폴더나 드라이브를 선택하세요.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"검색 위치를 찾을 수 없습니다: {root}");
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("인덱스 저장 위치를 확인할 수 없습니다.");
        }

        var database = Path.GetFullPath(DatabasePath);
        if (validateDatabaseOutsideRoot && IsWithin(database, root))
        {
            throw DatabaseInsideRootException();
        }

        return new FileSearchScope(root, database);
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedPath, normalizedRoot, comparison)
            || normalizedPath.StartsWith(prefix, comparison);
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

    private static InvalidOperationException DatabaseInsideRootException() =>
        new("선택한 검색 위치 안에 인덱스 DB가 있습니다. 더 좁은 위치를 선택하거나 '인덱스 위치'에서 DB를 검색 범위 밖의 로컬 디스크로 옮기세요.");

    private static void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("인덱스 DB의 상위 폴더를 확인할 수 없습니다.");
        }

        Directory.CreateDirectory(directory);
    }

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
            var rootChanged = !string.Equals(RootPath, eventArgs.Current.RootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(DatabasePath, eventArgs.Current.DatabasePath, StringComparison.OrdinalIgnoreCase);
            if (rootChanged)
            {
                InvalidateScope();
            }

            RootPath = eventArgs.Current.RootPath;
            DatabasePath = eventArgs.Current.DatabasePath;
            if (rootChanged)
            {
                Items.Clear();
                HasSearched = false;
                _ = InitializeAsync();
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(Apply);
        }
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
        InvalidateSearch();
    }
}
