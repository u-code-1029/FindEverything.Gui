using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Application.Options;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ILanguageSelectionService _languageSelectionService;
    private readonly IAppLocalizer _localizer;
    private readonly ISnackbarService _snackbarService;

    public MainWindowViewModel(
        CatalogActivityState catalog,
        ScanConsoleViewModel scanConsole,
        INavigationService navigationService,
        ILanguageSelectionService languageSelectionService,
        IAppLocalizer localizer,
        ISnackbarService snackbarService,
        OperationNotificationService? notifications = null)
    {
        Catalog = catalog;
        ScanConsole = scanConsole;
        _languageSelectionService = languageSelectionService;
        _localizer = localizer;
        _snackbarService = snackbarService;
        Notifications = notifications;
        ToggleScanConsoleCommand = new RelayCommand(ToggleScanConsole, () => IsCatalogPage);
        ChangeLanguageCommand = new AsyncRelayCommand<string>(ChangeLanguageAsync);
        ApplyPendingLanguageCommand = new AsyncRelayCommand(() => ChangeLanguageAsync(_languageSelectionService.PendingCultureName));
        _languageSelectionService.PendingChanged += OnPendingLanguageChanged;
        Catalog.PropertyChanged += OnActivityChanged;
    }

    public string ApplicationTitle { get; } = "FindEverything";

    public CatalogActivityState Catalog { get; }

    public ScanConsoleViewModel ScanConsole { get; }
    public OperationNotificationService? Notifications { get; }
    private bool _isCatalogPage;
    public bool IsCatalogPage
    {
        get => _isCatalogPage;
        set { if (SetProperty(ref _isCatalogPage, value)) ToggleScanConsoleCommand.NotifyCanExecuteChanged(); }
    }
    public bool HasPendingLanguage => _languageSelectionService.PendingCultureName is not null;
    public string PendingLanguageLabel => (_languageSelectionService.PendingCultureName == "en-US" ? "EN" : "KO")
        + " · " + _localizer.Get("Loc.Ux.Shell.Language.Pending", "적용 대기");
    public string PendingLanguageHint => _localizer.Get("Loc.Ux.Shell.Language.Pending.Help", "선택한 언어가 저장되었습니다. 작업이 끝나면 눌러서 다시 시작하세요. 자동으로 재시작하지 않습니다.");
    public bool IsAnyScanRunning => Catalog.IsScanning || Catalog.IsFilesScanning;
    public string ScanActivityText => Catalog.IsFilesScanning
        ? _localizer.Get("Loc.Ux.Shell.Scan.Files", "파일 스캔 중")
        : _localizer.Get("Loc.Ux.Shell.Scan.Catalog", "구조화 스캔 중");
    public string KoreanSelectionState => IsKorean ? _localizer.Get("Loc.Ux.Shell.Language.Selected", "현재 언어") : string.Empty;
    public string EnglishSelectionState => IsEnglish ? _localizer.Get("Loc.Ux.Shell.Language.Selected", "현재 언어") : string.Empty;

    public string CurrentCultureName => _languageSelectionService.CurrentCultureName;

    public bool IsKorean => string.Equals(
        CurrentCultureName,
        LocalizationOptions.KoreanCultureName,
        StringComparison.OrdinalIgnoreCase);

    public bool IsEnglish => string.Equals(
        CurrentCultureName,
        LocalizationOptions.EnglishCultureName,
        StringComparison.OrdinalIgnoreCase);

    public string LanguageRestartHint => _localizer.Get(
        "Loc.Ux.Shell.Language.Hint",
        "언어를 선택한 뒤 지금 다시 시작하거나 나중에 적용할 수 있습니다.");

    public IAsyncRelayCommand<string> ChangeLanguageCommand { get; }
    public IAsyncRelayCommand ApplyPendingLanguageCommand { get; }

    /// <summary>
    /// The console is available only on its owning page and never navigates as
    /// a side effect of opening a panel.
    /// </summary>
    public IRelayCommand ToggleScanConsoleCommand { get; }

    private void OnPendingLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingLanguage));
        OnPropertyChanged(nameof(PendingLanguageLabel));
    }
    private void OnActivityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsAnyScanRunning));
        OnPropertyChanged(nameof(ScanActivityText));
    }
    public void Dispose()
    {
        _languageSelectionService.PendingChanged -= OnPendingLanguageChanged;
        Catalog.PropertyChanged -= OnActivityChanged;
    }

    private async Task ChangeLanguageAsync(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return;
        }

        try
        {
            await _languageSelectionService.ChangeAsync(cultureName).ConfigureAwait(true);
        }
        catch (Exception)
        {
            _snackbarService.Show(
                _localizer.Get("Loc.Language.ChangeFailed.Title", "언어 변경 실패"),
                UserFacingExceptionLocalizer.TranslateLanguageChangeFailure(_localizer),
                ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(5));
        }
    }

    private void ToggleScanConsole()
    {
        if (!IsCatalogPage) return;
        ScanConsole.TogglePanelCommand.Execute(null);
    }
}
