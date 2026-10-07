using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    private readonly INavigationService _navigationService;

    public MainWindowViewModel(
        CatalogViewModel catalog,
        ScanConsoleViewModel scanConsole,
        INavigationService navigationService)
    {
        Catalog = catalog;
        ScanConsole = scanConsole;
        _navigationService = navigationService;
        ToggleScanConsoleCommand = new RelayCommand(ToggleScanConsole);
    }

    public string ApplicationTitle { get; } = "FindEverything";

    public CatalogViewModel Catalog { get; }

    public ScanConsoleViewModel ScanConsole { get; }

    /// <summary>
    /// The console belongs to CatalogPage rather than NavigationView. When the
    /// title-bar button is used from another page, navigate to its owning page
    /// first so the click always has an immediate, visible result.
    /// </summary>
    public IRelayCommand ToggleScanConsoleCommand { get; }

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
