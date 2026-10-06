using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
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
    private readonly IValidatedSettingsState<WorkspaceOptions> _workspaceSettings;
    private readonly IValidatedSettingsState<IndexingOptions> _indexingSettings;
    private readonly IValidatedSettingsState<AppearanceOptions> _appearanceSettings;
    private readonly IUserSettingsWriter _settingsWriter;
    private readonly IDesktopPickerService _pickerService;
    private readonly IPathLauncher _pathLauncher;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly ISnackbarService _snackbarService;
    private readonly ILogger<CatalogViewModel> _logger;
    private CatalogItemViewModel[] _loadedItems = [];

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
    private ListCollectionView _items = new(Array.Empty<CatalogItemViewModel>());

    [ObservableProperty]
    private CatalogItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _hasLoadedItems;

    [ObservableProperty]
    private string _filterSummary = "0개 항목";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressMessage = "프로필과 검색 위치를 선택하세요.";

    [ObservableProperty]
    private string _statusTitle = "준비";

    [ObservableProperty]
    private string _statusMessage = "인덱스를 만들거나 기존 인덱스를 불러올 수 있습니다.";

    [ObservableProperty]
    private bool _isStatusOpen = true;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public CatalogViewModel(
        ICatalogService catalogService,
        IProfileCatalog profileCatalog,
        IValidatedSettingsState<WorkspaceOptions> workspaceSettings,
        IValidatedSettingsState<IndexingOptions> indexingSettings,
        IValidatedSettingsState<AppearanceOptions> appearanceSettings,
        IUserSettingsWriter settingsWriter,
        IDesktopPickerService pickerService,
        IPathLauncher pathLauncher,
        IApplicationOperationCoordinator operationCoordinator,
        ISnackbarService snackbarService,
        ILogger<CatalogViewModel> logger)
    {
        _catalogService = catalogService;
        _profileCatalog = profileCatalog;
        _workspaceSettings = workspaceSettings;
        _indexingSettings = indexingSettings;
        _appearanceSettings = appearanceSettings;
        _settingsWriter = settingsWriter;
        _pickerService = pickerService;
        _pathLauncher = pathLauncher;
        _operationCoordinator = operationCoordinator;
        _snackbarService = snackbarService;
        _logger = logger;

        var workspace = workspaceSettings.Current;
        RootPath = workspace.RootPath;
        DatabasePath = workspace.DatabasePath;
        ApplyProfileSnapshot(profileCatalog.Current, workspace.SelectedProfileId);
        _profileCatalog.Changed += OnProfileCatalogChanged;

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
        Fields = value?.Profile.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .ToArray() ?? [];
        _loadedItems = [];
        Items = CreateItemsView([]);
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
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyChangedProfileSnapshot(eventArgs.Current);
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(() =>
                ApplyChangedProfileSnapshot(eventArgs.Current));
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
    private Task ScanAsync() => RunOperationAsync("인덱싱 및 불러오기", scanFirst: true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task LoadAsync() => RunOperationAsync("불러오기", scanFirst: false);

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

    private async Task RunOperationAsync(string operationName, bool scanFirst)
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
        SetStatus(operationName, $"{operationName} 작업을 시작했습니다.", InfoBarSeverity.Informational);
        var progress = CreateProgress();
        CatalogResult? result = null;
        try
        {
            // Capture UI-bound values before the coordinator moves the operation
            // to its background scheduler.
            var workspace = ValidateWorkspace();
            var request = CreateRequest(workspace);
            await _operationCoordinator.RunAsync(async cancellationToken =>
            {
                await PersistWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
                EnsureDatabaseDirectory(workspace.DatabasePath);

                result = scanFirst
                    ? await _catalogService.ScanAndLoadAsync(
                        request,
                        progress,
                        cancellationToken).ConfigureAwait(false)
                    : await _catalogService.LoadExistingAsync(
                        request,
                        progress,
                        cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(true);

            ApplyResult(
                result ?? throw new InvalidOperationException("카탈로그 결과를 받지 못했습니다."),
                scanFirst);
        }
        catch (OperationCanceledException)
        {
            SetStatus("취소됨", $"{operationName} 작업이 취소되었습니다.", InfoBarSeverity.Warning);
            ProgressMessage = "취소됨";
            ShowSnackbar("작업 취소", $"{operationName} 작업을 취소했습니다.", ControlAppearance.Caution);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "{OperationName} operation failed.", operationName);
            SetStatus("오류", exception.Message, InfoBarSeverity.Error);
            ProgressMessage = "작업 실패";
            ShowSnackbar($"{operationName} 실패", exception.Message, ControlAppearance.Danger);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static CatalogRequest CreateRequest(WorkspaceSelection workspace) =>
        new(workspace.ProfileId, workspace.RootPath, workspace.DatabasePath);

    private IProgress<CatalogOperationProgress> CreateProgress() =>
        new Progress<CatalogOperationProgress>(value => ProgressMessage = value.Message);

    private void ApplyResult(CatalogResult result, bool scanned)
    {
        var selectedPath = SelectedItem?.FullPath;
        var fields = result.Profile.Fields.OrderBy(static field => field.Order).ToArray();
        Items = CreateItemsView([]);
        Fields = fields;
        _loadedItems = result.Items
            .Select(item => new CatalogItemViewModel(item, fields))
            .ToArray();
        Items = CreateItemsView(_loadedItems);
        HasLoadedItems = _loadedItems.Length > 0;
        ApplyFilter(selectedPath);
        ProgressMessage = $"{_loadedItems.Length:N0}개 항목을 불러왔습니다.";

        var scanIncomplete = result.ScanReport is { } scanReport
            && (scanReport.Status != IndexScanStatus.Completed
                || scanReport.Errors.Count > 0);
        var incomplete = result.HasPendingScopes || scanIncomplete;
        var severity = incomplete
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success;
        var scanSummary = result.ScanReport is null
            ? string.Empty
            : $" · 인덱싱 상태 {result.ScanReport.Status} · 오류 {result.ScanReport.Errors.Count:N0}";
        SetStatus(
            incomplete ? "부분 결과" : scanned ? "인덱싱 및 불러오기 완료" : "불러오기 완료",
            $"후보 {result.CandidateCount:N0} · 일치 {result.Items.Count:N0} · 규칙 외 {result.NoMatchCount:N0} · 변환 오류 {result.InvalidItems.Count:N0}{scanSummary}"
                + (result.HasPendingScopes ? " · 아직 인덱싱되지 않은 범위가 있습니다." : string.Empty),
            severity);
        ShowSnackbar(
            scanned ? "인덱싱 및 불러오기 완료" : "불러오기 완료",
            $"프로필 규칙에 맞는 {result.Items.Count:N0}개 폴더를 찾았습니다.",
            incomplete ? ControlAppearance.Caution : ControlAppearance.Success);
    }

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
            ? $"{_loadedItems.Length:N0}개 항목"
            : $"{Items.Count:N0} / {_loadedItems.Length:N0}개 항목";
    }

    private ListCollectionView CreateItemsView(CatalogItemViewModel[] items)
    {
        var view = new ListCollectionView(items)
        {
            Filter = value => value is CatalogItemViewModel item && item.Matches(FilterText),
        };
        return view;
    }

    private WorkspaceSelection ValidateWorkspace()
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

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("인덱스 데이터베이스 경로를 선택하세요.");
        }

        var databasePath = Path.GetFullPath(DatabasePath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (string.Equals(databasePath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || databasePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "인덱스 데이터베이스는 검색 루트 밖에 저장해야 합니다.");
        }

        return new WorkspaceSelection(SelectedProfile.Id, normalizedRoot, databasePath);
    }

    private async Task PersistWorkspaceAsync(
        WorkspaceSelection workspace,
        CancellationToken cancellationToken)
    {
        var indexing = _indexingSettings.Current;
        var appearance = _appearanceSettings.Current;
        await _settingsWriter.SaveAsync(
            new UserSettingsUpdate(
                new WorkspaceOptions
                {
                    SelectedProfileId = workspace.ProfileId,
                    RootPath = workspace.RootPath,
                    DatabasePath = workspace.DatabasePath,
                },
                new IndexingOptions
                {
                    SearchPageSize = indexing.SearchPageSize,
                    MaxEntriesPerSecond = indexing.MaxEntriesPerSecond,
                    DirectoryDelayMilliseconds = indexing.DirectoryDelayMilliseconds,
                },
                new AppearanceOptions
                {
                    Theme = appearance.Theme,
                    Backdrop = appearance.Backdrop,
                }),
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

    private sealed record WorkspaceSelection(
        string ProfileId,
        string RootPath,
        string DatabasePath);
}
