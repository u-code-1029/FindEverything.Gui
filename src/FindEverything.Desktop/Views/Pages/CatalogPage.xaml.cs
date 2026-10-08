using System.Windows.Input;
using System.Windows.Controls.Primitives;
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

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.FocusedElement is not TextBoxBase
            && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox
            && _viewModel.CopySelectionCommand.CanExecute(null))
        {
            _viewModel.CopySelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPageLoaded(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.Activate();

    private void OnPageUnloaded(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.Deactivate();

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

    private void OnResultsGridSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateSelectionSnapshot();

    private void OnResultsGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, source)
                is not System.Windows.Controls.DataGridRow row)
        {
            return;
        }

        if (!row.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        ResultsGrid.SelectedItem = row.Item;
        row.Focus();
    }

    private void OnSelectAllClick(object sender, System.Windows.RoutedEventArgs e) =>
        ResultsGrid.SelectAll();

    private void OnClearSelectionClick(object sender, System.Windows.RoutedEventArgs e) =>
        ResultsGrid.UnselectAll();

    private void OnInvertSelectionClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = ResultsGrid.SelectedItems.Cast<object>().ToHashSet();
        ResultsGrid.SelectionChanged -= OnResultsGridSelectionChanged;
        try
        {
            ResultsGrid.SelectedItems.Clear();
            foreach (var item in ResultsGrid.Items.Cast<object>())
            {
                if (!selected.Contains(item))
                {
                    ResultsGrid.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            ResultsGrid.SelectionChanged += OnResultsGridSelectionChanged;
            UpdateSelectionSnapshot();
        }
    }

    private void UpdateSelectionSnapshot()
    {
        var selected = ResultsGrid.SelectedItems.Cast<object>().ToHashSet();
        _viewModel.SetSelection(ResultsGrid.Items
            .Cast<CatalogItemViewModel>()
            .Where(selected.Contains));
    }
}
