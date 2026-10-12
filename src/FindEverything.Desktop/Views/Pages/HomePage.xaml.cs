using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class HomePage
{
    private const double TwoColumnBreakpoint = 760;
    private const double CardGap = 12;

    public HomePage(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnDashboardContentSizeChanged(
        object sender,
        System.Windows.SizeChangedEventArgs eventArgs)
    {
        var availableWidth = Math.Max(0, eventArgs.NewSize.Width);
        var cardWidth = availableWidth >= TwoColumnBreakpoint
            ? Math.Max(280, (availableWidth - (CardGap * 2)) / 2)
            : Math.Max(0, availableWidth - CardGap);

        FilesActionCard.Width = cardWidth;
        CatalogActionCard.Width = cardWidth;
        WorkspaceOverviewCard.Width = cardWidth;
        SetupOverviewCard.Width = cardWidth;
        HeroVisual.Visibility = availableWidth < 680
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }
}
