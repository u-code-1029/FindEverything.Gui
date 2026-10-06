using System.Windows.Input;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class CatalogPage
{
    private readonly CatalogViewModel _viewModel;

    public CatalogPage(CatalogViewModel viewModel, IGridLayoutStore gridLayoutStore)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        DynamicProfileGrid.SetLayoutStore(ResultsGrid, gridLayoutStore);
    }

    private void OnResultsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is CatalogItemViewModel item
            && _viewModel.OpenItemCommand.CanExecute(item))
        {
            _viewModel.OpenItemCommand.Execute(item);
        }
    }
}
