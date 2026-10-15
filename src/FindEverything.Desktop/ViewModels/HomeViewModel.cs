using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceContext _workspaceContext;
    private readonly IProfileCatalog _profileCatalog;
    private readonly IIndexDatabasePathResolver _databasePathResolver;
    private readonly IApplicationOperationCoordinator _operationCoordinator;
    private readonly IHomeNavigationService _navigationService;
    private readonly IAppLocalizer _localizer;
    private readonly IUiScheduler _uiScheduler;
    private bool _disposed;
    private readonly IDesktopPickerService? _picker;
    private readonly WorkspaceTransferViewModel? _transfer;
    private readonly Wpf.Ui.ISnackbarService? _notifications;
    private readonly IProfilePlaygroundNavigator? _playgroundNavigator;
    private string? _displayedLocalProfileId;

    [ObservableProperty]
    private bool _hasRoot;

    [ObservableProperty]
    private bool _hasProfile;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _isOperationRunning;

    [ObservableProperty]
    private string _rootPathText = string.Empty;

    [ObservableProperty]
    private string _rootStateText = string.Empty;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _profileStateText = string.Empty;

    [ObservableProperty]
    private string _profileCountText = string.Empty;

    [ObservableProperty]
    private string _fileDatabasePath = string.Empty;

    [ObservableProperty]
    private string _fileDatabaseStateText = string.Empty;

    [ObservableProperty]
    private string _profileDatabasePath = string.Empty;

    [ObservableProperty]
    private string _profileDatabaseStateText = string.Empty;

    [ObservableProperty]
    private string _setupTitle = string.Empty;

    [ObservableProperty]
    private string _setupMessage = string.Empty;

    [ObservableProperty]
    private string _locationStepState = string.Empty;

    [ObservableProperty]
    private string _profileStepState = string.Empty;

    [ObservableProperty]
    private string _scanStepState = string.Empty;

    [ObservableProperty]
    private string _operationStatus = string.Empty;

    public HomeViewModel(
        IWorkspaceContext workspaceContext,
        IProfileCatalog profileCatalog,
        IIndexDatabasePathResolver databasePathResolver,
        IApplicationOperationCoordinator operationCoordinator,
        IHomeNavigationService navigationService,
        IAppLocalizer localizer,
        IUiScheduler? uiScheduler = null,
        IDesktopPickerService? picker = null,
        WorkspaceTransferViewModel? transfer = null,
        Wpf.Ui.ISnackbarService? notifications = null,
        IProfilePlaygroundNavigator? playgroundNavigator = null)
    {
        _workspaceContext = workspaceContext;
        _profileCatalog = profileCatalog;
        _databasePathResolver = databasePathResolver;
        _operationCoordinator = operationCoordinator;
        _navigationService = navigationService;
        _localizer = localizer;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;
        _picker = picker;
        _transfer = transfer;
        _notifications = notifications;
        _playgroundNavigator = playgroundNavigator;
        if (_transfer is not null) _transfer.SourceChanged += OnSourceChanged;

        _workspaceContext.Changed += OnWorkspaceChanged;
        _profileCatalog.Changed += OnProfileCatalogChanged;
        _operationCoordinator.StateChanged += OnOperationStateChanged;
        RefreshDashboard(_workspaceContext.Current, _profileCatalog.Current);
        ApplyOperationState(_operationCoordinator.CurrentKind);
    }

    [RelayCommand]
    private void OpenFiles() => _navigationService.Navigate(typeof(FilesPage));

    [RelayCommand]
    private void OpenCatalog() => _navigationService.Navigate(typeof(CatalogPage));

    [RelayCommand]
    private void OpenStructuredWorkspace() => _navigationService.Navigate(
        HasProfile || IsSharedSource ? typeof(CatalogPage) : typeof(ProfilesPage));

    [RelayCommand]
    private void OpenProfiles() => _navigationService.Navigate(typeof(ProfilesPage));

    [RelayCommand]
    private void OpenPlayground()
    {
        // A shared source contains a frozen profile, not an editable local one.
        // Never silently test an unrelated local profile from that workspace.
        if (IsSharedSource) return;
        if (_playgroundNavigator is not null && _displayedLocalProfileId is { } profileId)
        {
            _playgroundNavigator.Navigate(profileId);
            return; // A removed profile must not fall back to a stale test selection.
        }
        _navigationService.Navigate(typeof(ProfilePlaygroundPage));
    }

    [RelayCommand]
    private void OpenOutputFormats() => _navigationService.Navigate(typeof(OutputFormatsPage));

    public bool IsSharedSource => _transfer?.IsImported == true;
    public bool NeedsLocation => !HasRoot && !IsSharedSource;
    public bool HasCurrentLocation => HasRoot || IsSharedSource;
    public bool ShowProfileTools => HasProfile && !IsSharedSource;
    public string NextStepTitle => IsSharedSource
        ? L("Loc.Ux.Home.Next.Shared", "공유 결과를 확인하세요")
        : NeedsLocation
            ? L("Loc.Ux.Home.Next.Location", "검색할 위치를 먼저 선택하세요")
            : !HasProfile
                ? L("Loc.Ux.Home.Next.Files", "파일 찾기를 바로 시작할 수 있어요")
                : L("Loc.Ux.Home.Next.Ready", "이 위치에서 파일을 찾으세요");
    public string NextStepMessage => IsSharedSource
        ? L("Loc.Ux.Home.Next.Shared.Help", "가져온 인덱스는 읽기 전용입니다. 내 디스크를 스캔하려면 이 PC의 작업 환경으로 전환하세요.")
        : NeedsLocation
            ? L("Loc.Ux.Home.Next.Location.Help", "드라이브나 폴더를 선택한 다음 파일 찾기에서 스캔하세요. 프로필은 없어도 됩니다.")
            : !HasProfile
                ? L("Loc.Ux.Home.Next.Files.Help", "전체 파일은 파일 찾기에서 볼 수 있습니다. 폴더 경로의 정보를 컬럼으로 보려면 프로필을 만드세요.")
                : L("Loc.Ux.Home.Next.Ready.Help", "파일 찾기에서 전체 파일을 검색하거나, 구조화 보기에서 프로필에 맞는 정보를 확인하세요.");
    public string StructuredActionTitle => HasProfile || IsSharedSource
        ? L("Loc.Home.Catalog.Title", "구조화 보기")
        : L("Loc.Ux.Home.CreateProfile", "프로필 만들기");
    public string StructuredActionDescription => IsSharedSource
        ? L("Loc.Ux.Home.SharedCatalog.Help", "공유된 프로필의 구조화 보기를 엽니다.")
        : HasProfile
            ? L("Loc.Ux.Shell.Home.Catalog", "폴더 경로에서 정보를 추출해 컬럼으로 비교합니다.")
            : L("Loc.Ux.Home.CreateProfile.Help", "예제 경로에서 필요한 조각을 선택해 이름, 날짜 같은 컬럼을 만듭니다.");
    public string CurrentLocation => _transfer?.CurrentSource?.EffectiveRoot ?? RootPathText;
    public string CurrentSourceName => _transfer?.SelectedSource?.DisplayName
        ?? L("Loc.Ux.Shell.Home.Local", "이 PC의 작업 환경");
    public bool CanChooseLocation => !IsOperationRunning && !IsSharedSource && _picker is not null;
    [RelayCommand(CanExecute = nameof(CanChooseLocation))]
    private async Task ChooseLocationAsync()
    {
        if (!CanChooseLocation) return;
        try
        {
            var selected = _picker!.PickRootDirectory(_workspaceContext.Current.RootPath);
            if (selected is null || !CanChooseLocation) return;
            await _workspaceContext.UpdateAsync(current => current with { RootPath = selected }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _notifications?.Show(L("Loc.Ux.Shell.Home.LocationFailed", "검색 위치를 저장하지 못했습니다."), exception.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.Zero);
        }
    }
    [RelayCommand]
    private void UseLocalSource() => _transfer?.UseLocalCommand.Execute(null);
    private void OnSourceChanged(object? sender, EventArgs e) => Dispatch(RefreshSource);
    private void RefreshSource()
    {
        OnPropertyChanged(nameof(IsSharedSource)); OnPropertyChanged(nameof(CurrentLocation));
        OnPropertyChanged(nameof(CurrentSourceName)); OnPropertyChanged(nameof(CanChooseLocation));
        OnPropertyChanged(nameof(NeedsLocation)); OnPropertyChanged(nameof(HasCurrentLocation));
        OnPropertyChanged(nameof(ShowProfileTools)); OnPropertyChanged(nameof(NextStepTitle));
        OnPropertyChanged(nameof(NextStepMessage)); OnPropertyChanged(nameof(StructuredActionTitle));
        OnPropertyChanged(nameof(StructuredActionDescription));
        ChooseLocationCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspaceContext.Changed -= OnWorkspaceChanged;
        _profileCatalog.Changed -= OnProfileCatalogChanged;
        _operationCoordinator.StateChanged -= OnOperationStateChanged;
        if (_transfer is not null) _transfer.SourceChanged -= OnSourceChanged;
        GC.SuppressFinalize(this);
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs eventArgs) =>
        Dispatch(() => RefreshDashboard(eventArgs.Current, _profileCatalog.Current));

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs) =>
        Dispatch(() => RefreshDashboard(_workspaceContext.Current, eventArgs.Current));

    private void OnOperationStateChanged(
        object? sender,
        ApplicationOperationStateChangedEventArgs eventArgs) =>
        Dispatch(() =>
        {
            ApplyOperationState(eventArgs.CurrentKind);
            if (eventArgs.CurrentKind is null)
            {
                RefreshDashboard(_workspaceContext.Current, _profileCatalog.Current);
            }
        });

    private void RefreshDashboard(
        WorkspaceSnapshot workspace,
        ProfileCatalogSnapshot catalog)
    {
        HasRoot = !string.IsNullOrWhiteSpace(workspace.RootPath);
        RootPathText = HasRoot
            ? workspace.RootPath!
            : L("Loc.Home.State.Root.Empty", "아직 검색 위치를 선택하지 않았습니다.");
        RootStateText = HasRoot
            ? L("Loc.Home.State.Configured", "설정됨")
            : L("Loc.Home.State.Required", "선택 필요");

        var selectedProfile = ResolveSelectedProfile(workspace.SelectedProfileId, catalog);
        _displayedLocalProfileId = selectedProfile?.Descriptor.Id;
        HasProfile = selectedProfile is not null;
        ProfileName = selectedProfile?.Descriptor.DisplayName
            ?? L("Loc.Home.State.Profile.Empty", "아직 사용할 프로필이 없습니다.");
        ProfileStateText = HasProfile
            ? selectedProfile!.Descriptor.Id
            : L("Loc.Home.State.Required", "선택 필요");
        ProfileCountText = F(
            "Loc.Home.State.ProfileCount",
            "사용 가능한 프로필 {0:N0}개",
            catalog.Profiles.Count);

        FileDatabasePath = _databasePathResolver.ResolveFileSearchDatabasePath(workspace);
        FileDatabaseStateText = BuildDatabaseState(
            string.IsNullOrWhiteSpace(workspace.DatabasePath));

        if (selectedProfile is null)
        {
            ProfileDatabasePath = L(
                "Loc.Home.State.ProfileDatabase.Pending",
                "프로필을 선택하면 전용 DB 경로가 자동으로 정해집니다.");
            ProfileDatabaseStateText = L("Loc.Home.State.Waiting", "대기 중");
        }
        else
        {
            ProfileDatabasePath = _databasePathResolver.ResolveProfileDatabasePath(
                selectedProfile.Descriptor.Id,
                workspace);
            ProfileDatabaseStateText = BuildDatabaseState(
                !HasProfileDatabaseOverride(workspace, selectedProfile.Descriptor.Id));
        }

        IsReady = HasRoot && HasProfile;
        SetupTitle = IsReady
            ? L("Loc.Home.State.Ready.Title", "구조화 스캔 설정 완료")
            : L("Loc.Home.State.Setup.Title", "몇 가지만 준비하면 시작할 수 있어요");
        SetupMessage = IsReady
            ? L(
                "Loc.Home.State.Ready.Message",
                "검색 위치와 프로필이 연결되었습니다. 구조화 보기에서 접근 상태를 확인한 뒤 스캔을 시작하세요.")
            : L(
                "Loc.Home.State.Setup.Message",
                "검색 위치와 프로필을 준비하세요. 파일 찾기는 프로필 없이도 사용할 수 있습니다.");
        LocationStepState = HasRoot
            ? L("Loc.Common.Completed", "완료")
            : L("Loc.Home.State.Required", "선택 필요");
        ProfileStepState = HasProfile
            ? L("Loc.Common.Completed", "완료")
            : L("Loc.Home.State.Required", "선택 필요");
        ScanStepState = IsReady
            ? L("Loc.Common.Ready", "준비")
            : L("Loc.Home.State.Waiting", "대기 중");
        RefreshSource();
    }

    private void ApplyOperationState(ApplicationOperationKind? operationKind)
    {
        IsOperationRunning = operationKind is not null;
        OnPropertyChanged(nameof(CanChooseLocation));
        ChooseLocationCommand.NotifyCanExecuteChanged();
        OperationStatus = operationKind switch
        {
            ApplicationOperationKind.IndexWrite => L(
                "Loc.Home.Operation.Scanning",
                "디스크를 스캔하고 검색 DB를 갱신하고 있습니다."),
            ApplicationOperationKind.IndexLoad => L(
                "Loc.Home.Operation.Loading",
                "저장된 검색 DB에서 결과를 불러오고 있습니다."),
            ApplicationOperationKind.ProfileDiscovery => L(
                "Loc.Home.Operation.ProfileDiscovery",
                "사용 가능한 프로필을 불러오고 있습니다."),
            ApplicationOperationKind.ProfileWrite => L(
                "Loc.Home.Operation.ProfileWrite",
                "프로필을 검증하고 저장하고 있습니다."),
            ApplicationOperationKind.Exclusive => L(
                "Loc.Home.Operation.Working",
                "요청한 작업을 처리하고 있습니다."),
            _ => string.Empty,
        };
    }

    // Do not probe this path on the UI thread. In particular, File.Exists can
    // block for a disconnected UNC share and file existence alone does not prove
    // that the database contains a valid index for the configured root.
    private string BuildDatabaseState(bool usesDefaultPath) => usesDefaultPath
        ? L("Loc.Home.State.Database.Default", "자동 저장 위치")
        : L("Loc.Home.State.Database.Custom", "사용자 지정 저장 위치");

    private static ILoadedProfile? ResolveSelectedProfile(
        string? selectedProfileId,
        ProfileCatalogSnapshot catalog) =>
        !string.IsNullOrWhiteSpace(selectedProfileId)
            && catalog.TryGetProfile(selectedProfileId, out var selected)
                ? selected
                : catalog.Profiles.FirstOrDefault();

    private static bool HasProfileDatabaseOverride(
        WorkspaceSnapshot workspace,
        string profileId) =>
        workspace.ProfileDatabasePaths?.Any(pair =>
            string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(pair.Value)) == true;

    private void Dispatch(Action action) => _uiScheduler.Post(() =>
    {
        if (!_disposed) action();
    });

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer.Format(key, koreanFallback, arguments);
}
