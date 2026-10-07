using System.Windows.Input;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class CatalogPage
{
    private readonly CatalogViewModel _viewModel;

    public CatalogPage(
        CatalogViewModel viewModel,
        ScanConsoleViewModel scanConsoleViewModel,
        IGridLayoutStore gridLayoutStore)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ScanConsolePanel.DataContext = scanConsoleViewModel;
        DynamicProfileGrid.SetLayoutStore(ResultsGrid, gridLayoutStore);
    }

    private void OnResultsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, source)
                is not System.Windows.Controls.DataGridRow row
            || row.Item is not CatalogItemViewModel item)
        {
            return;
        }

        ResultsGrid.SelectedItem = item;
        if (_viewModel.OpenItemCommand.CanExecute(item))
        {
            _viewModel.OpenItemCommand.Execute(item);
        }
    }
}
