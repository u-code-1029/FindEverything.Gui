using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.ViewModels;
using Wpf.Ui;

namespace FindEverything.Desktop.Views;

public partial class MainWindow
{
    private readonly IAppearanceService _appearanceService;
    private readonly IValidatedSettingsState<AppearanceOptions> _appearanceSettings;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppearanceService appearanceService,
        IValidatedSettingsState<AppearanceOptions> appearanceSettings)
    {
        _appearanceService = appearanceService;
        _appearanceSettings = appearanceSettings;
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(DialogHost);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _appearanceService.Attach(this);
        _appearanceService.Apply(this, _appearanceSettings.Current);
    }
}
