using System.Windows;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class CatalogPage
{
    private readonly IProfilePlaygroundNavigator? _playgroundNavigator;

    private void OnColumnsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor) DynamicProfileGrid.ShowColumnsMenu(ResultsGrid, anchor);
    }

    private void OnFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CatalogActiveColumnFilter filter } anchor)
            DynamicProfileGrid.OpenColumnFilter(ResultsGrid, filter.Filter.Target, anchor);
    }

    private void OnEmptyTestProfileClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is { } profile) _playgroundNavigator?.Navigate(profile.Id);
    }

    private void OnEmptyLogClick(object sender, RoutedEventArgs e)
    {
        if (ScanConsolePanel.DataContext is ScanConsoleViewModel console) console.Show();
    }
}
