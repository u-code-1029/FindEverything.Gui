using System.Windows;
using System.Windows.Shell;
using System.ComponentModel;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views;

public partial class MainWindow
{
    private ShellLayoutMode? _shellLayoutMode;
    private readonly IDraftProtectionService _draftProtection;
    private readonly ISnackbarService _snackbars;
    private bool _closeConfirmed;
    private bool _confirmingClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppearanceService appearanceService,
        IValidatedSettingsState<AppearanceOptions> appearanceSettings,
        IDraftProtectionService draftProtection,
        OperationNotificationService? operationNotifications = null)
    {
        _draftProtection = draftProtection;
        _snackbars = snackbarService;
        DataContext = viewModel;

        InitializeComponent();
        OperationInfoBar.DataContext = operationNotifications ?? snackbarService as OperationNotificationService;

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(DialogHost);

        // Select the final backdrop before Show() creates the HWND. WPF-UI installs
        // WindowChrome from OnSourceInitialized, so changing None -> Auto after the
        // window is loaded would replace a live Freezable and can make WindowsBase
        // reject the old inheritance context.
        appearanceService.Attach(this);
        appearanceService.Apply(this, appearanceSettings.Current);

        ApplyAdaptiveShellLayout(Width);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_confirmingClose) return;
        _confirmingClose = true;
        try
        {
            if (await _draftProtection.ConfirmAllAsync())
            {
                _closeConfirmed = true;
                // Even an already-clean draft can complete synchronously. WPF
                // rejects a reentrant Close while this Closing event is active.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        catch (Exception)
        {
            _snackbars.Show(
                System.Windows.Application.Current.TryFindResource("Loc.Draft.Leave.Failed.Title") as string ?? "Could not check unsaved changes",
                System.Windows.Application.Current.TryFindResource("Loc.Draft.Leave.Failed.Message") as string ?? "Your draft has been preserved. Save it and try closing again.",
                ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle20),
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        ApplyAdaptiveShellLayout(eventArgs.NewSize.Width);
    }

    private void ApplyAdaptiveShellLayout(double width)
    {
        var state = AdaptiveShellLayout.Resolve(width);

        // Keep the two language actions and scan-console action available before
        // reserving title text. Window.Title remains populated for the taskbar and
        // assistive technology even when the visual title is omitted.
        AppTitleBar.Title = state.ShowApplicationTitle
            ? ((MainWindowViewModel)DataContext).ApplicationTitle
            : string.Empty;

        if (_shellLayoutMode == state.Mode)
        {
            return;
        }

        _shellLayoutMode = state.Mode;
        // WPF-UI 4.3 has no LeftCompact enum. Left + a closed pane is its
        // supported compact-rail state; unlike LeftMinimal it keeps the rail,
        // selection marker, and scan InfoBadge visible and aligned.
        RootNavigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        if (state.Mode == ShellLayoutMode.Compact)
        {
            RootNavigation.IsPaneOpen = false;
            return;
        }

        RootNavigation.IsPaneOpen = true;
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
