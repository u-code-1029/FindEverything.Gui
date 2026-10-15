using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class HomePage
{
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
        CatalogActionCard.Width = ImportProfileActionCard.Width = Math.Max(0,
            availableWidth >= 700 ? (availableWidth - CardGap * 2) / 2 : availableWidth - CardGap);
    }
}
