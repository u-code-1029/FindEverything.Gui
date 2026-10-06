using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class FilesPage
{
    private readonly FileSearchViewModel _viewModel;

    public FilesPage(FileSearchViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _viewModel.RefreshIndexCommand.CanExecute(null))
        {
            _viewModel.RefreshIndexCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsGrid, source) is not DataGridRow row)
        {
            return;
        }

        ResultsGrid.SelectedItem = row.Item;
        if (_viewModel.OpenSelectedCommand.CanExecute(null))
        {
            _viewModel.OpenSelectedCommand.Execute(null);
        }
    }

    private void OnResultsGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || e.OriginalSource is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 160
            && _viewModel.LoadMoreCommand.CanExecute(null))
        {
            _viewModel.LoadMoreCommand.Execute(null);
        }
    }

    private void OnResultsGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (!Enum.TryParse<EntrySortField>(e.Column.SortMemberPath, ignoreCase: true, out var field))
        {
            return;
        }

        e.Handled = true;
        var direction = _viewModel.SelectedSort.Value == field
            && _viewModel.SortDirection == EntrySortDirection.Ascending
                ? EntrySortDirection.Descending
                : EntrySortDirection.Ascending;
        foreach (var column in ResultsGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction == EntrySortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
        _viewModel.ApplySort(field, direction);
    }
}
