using System.Windows;
using System.Windows.Shell;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views;

public partial class MainWindow
{
    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppearanceService appearanceService,
        IValidatedSettingsState<AppearanceOptions> appearanceSettings)
    {
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(DialogHost);

        // Select the final backdrop before Show() creates the HWND. WPF-UI installs
        // WindowChrome from OnSourceInitialized, so changing None -> Auto after the
        // window is loaded would replace a live Freezable and can make WindowsBase
        // reject the old inheritance context.
        appearanceService.Attach(this);
        appearanceService.Apply(this, appearanceSettings.Current);
    }

    protected override void SetWindowChrome()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is null)
        {
            // Let FluentWindow perform its normal composition check and create the
            // first instance. Later calls update that same attached Freezable.
            base.SetWindowChrome();
            return;
        }

        // WPF-UI 4.3 normally creates a new WindowChrome on every backdrop change.
        // Updating the attached instance is supported by WindowChromeWorker and avoids
        // detaching the same Freezable inheritance context twice in WindowsBase.
        chrome.CaptionHeight = 0;
        chrome.CornerRadius = default;
        chrome.GlassFrameThickness = WindowBackdropType == WindowBackdropType.None
            ? new Thickness(0.00001)
            : new Thickness(-1);
        chrome.ResizeBorderThickness = ResizeMode == ResizeMode.NoResize
            ? default
            : new Thickness(4);
        chrome.UseAeroCaptionButtons = false;
    }
}
