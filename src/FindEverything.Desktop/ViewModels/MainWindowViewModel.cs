using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Options;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    private readonly INavigationService _navigationService;
    private readonly ILanguageSelectionService _languageSelectionService;
    private readonly IAppLocalizer _localizer;
    private readonly ISnackbarService _snackbarService;

    public MainWindowViewModel(
        CatalogViewModel catalog,
        ScanConsoleViewModel scanConsole,
        INavigationService navigationService,
        ILanguageSelectionService languageSelectionService,
        IAppLocalizer localizer,
        ISnackbarService snackbarService)
    {
        Catalog = catalog;
        ScanConsole = scanConsole;
        _navigationService = navigationService;
        _languageSelectionService = languageSelectionService;
        _localizer = localizer;
        _snackbarService = snackbarService;
        ToggleScanConsoleCommand = new RelayCommand(ToggleScanConsole);
        ChangeLanguageCommand = new AsyncRelayCommand<string>(ChangeLanguageAsync);
    }

    public string ApplicationTitle { get; } = "FindEverything";

    public CatalogViewModel Catalog { get; }

    public ScanConsoleViewModel ScanConsole { get; }

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
        "Loc.Language.RestartHint",
        "언어를 바꾸면 설정을 저장한 뒤 앱을 다시 시작합니다.");

    public IAsyncRelayCommand<string> ChangeLanguageCommand { get; }

    /// <summary>
    /// The console belongs to CatalogPage rather than NavigationView. When the
    /// title-bar button is used from another page, navigate to its owning page
    /// first so the click always has an immediate, visible result.
    /// </summary>
    public IRelayCommand ToggleScanConsoleCommand { get; }

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
        var navigation = _navigationService.GetNavigationControl();
        var currentPageType = (navigation?.SelectedItem as NavigationViewItem)?.TargetPageType;
        if (currentPageType != typeof(CatalogPage))
        {
            if (_navigationService.Navigate(typeof(CatalogPage)))
            {
                ScanConsole.Show();
            }

            return;
        }

        ScanConsole.TogglePanelCommand.Execute(null);
    }
}
