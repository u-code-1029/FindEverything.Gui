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
    private readonly HashSet<FrameworkElement> _notificationChrome = [];
    private bool _notificationPlacementPending;
    private bool _settingPaneLayout;
    private bool? _preferredPaneOpen;
    private readonly DependencyPropertyDescriptor _paneOpenDescriptor;
    private readonly EventHandler _paneOpenChanged;

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
        Loaded += (_, _) => UpdatePageNotificationSpace();
        RootNavigation.SelectionChanged += (_, _) =>
            viewModel.IsCatalogPage = (RootNavigation.SelectedItem as NavigationViewItem)?.TargetPageType == typeof(Pages.CatalogPage);
        NotificationHistoryFlyout.Opened += (_, _) =>
        {
            if (viewModel.Notifications is { } notices) notices.IsHistoryOpen = true;
            NotificationHistoryFlyout.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        };
        NotificationHistoryFlyout.Closed += (_, _) =>
        {
            if (viewModel.Notifications is { } notices) notices.IsHistoryOpen = false;
        };

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
        // 640 DIP exceeds a 1080p display's available height at 200% scaling.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height));
        _paneOpenDescriptor = DependencyPropertyDescriptor.FromProperty(NavigationView.IsPaneOpenProperty, typeof(NavigationView));
        _paneOpenChanged = (_, _) => { if (!_settingPaneLayout) _preferredPaneOpen = RootNavigation.IsPaneOpen; };
        _paneOpenDescriptor.AddValueChanged(RootNavigation, _paneOpenChanged);
        Closed += (_, _) => _paneOpenDescriptor.RemoveValueChanged(RootNavigation, _paneOpenChanged);
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
        UpdatePageNotificationSpace();
    }

    private void OnOperationInfoBarSizeChanged(object sender, SizeChangedEventArgs eventArgs)
        => UpdatePageNotificationSpace();

    private void OnOperationInfoBarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
        => UpdatePageNotificationSpace();

    private void OnNoticeInteractionChanged(object sender, System.Windows.Input.MouseEventArgs e) => UpdateNoticePause();
    private void OnNoticeFocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateNoticePause();
    private void UpdateNoticePause()
    {
        if (OperationInfoBar.DataContext is OperationNotificationService service)
            service.SetInteractionPaused(OperationInfoBar.IsMouseOver || OperationInfoBar.IsKeyboardFocusWithin);
    }
    private void OnNotificationsClick(object sender, RoutedEventArgs e)
    {
        NotificationHistoryFlyout.ApplyTemplate();
        if (NotificationHistoryFlyout.Template?.FindName("PART_Popup", NotificationHistoryFlyout) is not System.Windows.Controls.Primitives.Popup popup) return;
        popup.PlacementTarget = NotificationsButton;
        popup.StaysOpen = false;
        if (NotificationHistoryFlyout.IsOpen) NotificationHistoryFlyout.Hide(); else NotificationHistoryFlyout.Show();
    }
    private void OnNotificationHistoryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        NotificationHistoryFlyout.Hide(); NotificationsButton.Focus(); e.Handled = true;
    }

    private void UpdatePageNotificationSpace()
    {
        if (_notificationPlacementPending) return;
        _notificationPlacementPending = true;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _notificationPlacementPending = false;
            if (!OperationInfoBar.IsOpen || PageNotificationOverlay.ActualHeight <= 0) return;
            var bottom = 16d;
            foreach (var chrome in _notificationChrome)
            {
                if (!chrome.IsLoaded || !chrome.IsVisible || chrome.ActualHeight <= 0) continue;
                try
                {
                    var top = chrome.TranslatePoint(new Point(0, 0), PageNotificationOverlay).Y;
                    if (top >= 0 && top < PageNotificationOverlay.ActualHeight)
                        bottom = Math.Max(bottom, PageNotificationOverlay.ActualHeight - top + 16);
                }
                catch (InvalidOperationException) { /* A page may have just navigated away. */ }
            }
            var margin = new Thickness(16, 0, 16, bottom);
            if (OperationInfoBar.Margin != margin) OperationInfoBar.SetCurrentValue(MarginProperty, margin);
        }));
    }

    internal void RegisterNotificationChrome(FrameworkElement element)
    {
        _notificationChrome.Add(element);
        UpdatePageNotificationSpace();
    }

    internal void UnregisterNotificationChrome(FrameworkElement element)
    {
        _notificationChrome.Remove(element);
        UpdatePageNotificationSpace();
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
        _settingPaneLayout = true;
        try { RootNavigation.IsPaneOpen = _preferredPaneOpen ?? state.Mode != ShellLayoutMode.Compact; }
        finally { _settingPaneLayout = false; }
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
