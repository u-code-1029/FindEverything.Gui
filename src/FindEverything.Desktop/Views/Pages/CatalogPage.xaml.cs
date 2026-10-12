using System.Windows.Input;
using System.Windows.Controls.Primitives;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class CatalogPage
{
    private readonly CatalogViewModel _viewModel;
    private CatalogItemViewModel? _pointerContextItem;

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
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogViewModel.IsDetailsOpen)) ApplyWorkspaceLayout();
        };
    }

    private void OnWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ScanConsolePanel is null || CatalogResultsWorkspace is null) return;
        ScanConsolePanel.Height = Math.Max(120, CatalogPageLayout.ActualHeight * 0.3);
        ScanConsolePanel.MaxHeight = Math.Max(120, CatalogPageLayout.ActualHeight * 0.45);
        var reserveDetails = _viewModel.IsDetailsOpen && CatalogPageContent.ActualWidth >= 900;
        CatalogResultsWorkspace.Margin = new System.Windows.Thickness(0, 0, reserveDetails ? 348 : 0, 0);
        CatalogDetailsPanel.Width = Math.Min(340, Math.Max(200, CatalogPageContent.ActualWidth));
    }

    private async void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultsGrid.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.DependencyObject focused
            && !ResultGridInput.IsInteractiveOrigin(focused)
            && ResultsGrid.CurrentItem is CatalogItemViewModel { IsPlaceholder: false } current)
        {
            if (_viewModel.UsesQuerySelection && e.Key == Key.Space)
            {
                e.Handled = true;
                await _viewModel.SelectRowAsync(current, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                return;
            }
            if (e.Key == Key.Enter && _viewModel.OpenItemCommand.CanExecute(current))
            {
                e.Handled = true;
                _viewModel.OpenItemCommand.Execute(current);
                return;
            }
        }
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.FocusedElement is not TextBoxBase && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox)
        {
            _viewModel.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.FocusedElement is not TextBoxBase
            && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox
            && _viewModel.CopySelectionCommand.CanExecute(null))
        {
            _viewModel.CopySelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.F5 && _viewModel.ScanCommand.CanExecute(null))
        {
            _viewModel.ScanCommand.Execute(null); e.Handled = true;
        }
    }

    private void OnPageLoaded(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.Activate();

    private void OnPageUnloaded(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.Deactivate();

    private async void OnWorkspacePathLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel.CommitWorkspaceCommand.CanExecute(null))
        {
            await _viewModel.CommitWorkspaceCommand.ExecuteAsync(null);
        }
    }

    private void OnResultsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || ResultGridInput.IsInteractiveOrigin(source)
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

    private async void OnResultsGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UsesQuerySelection && e.OriginalSource is System.Windows.DependencyObject querySource
            && !ResultGridInput.IsInteractiveOrigin(querySource)
            && System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, querySource) is System.Windows.Controls.DataGridRow row)
        {
            e.Handled = true;
            if (row.Item is not CatalogItemViewModel { IsPlaceholder: false } item) return;
            ResultsGrid.SelectedItem = item;
            ResultsGrid.CurrentItem = item;
            row.Focus();
            await _viewModel.SelectRowAsync(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || FindAncestor<TextBoxBase>(source) is null)
        {
            return;
        }

        // Text input is an interactive child, not a row-selection gesture. Let
        // the TextBox receive the click/caret normally, then restore the exact
        // checkbox-backed selection that existed before the routed DataGrid click.
        var selected = ResultsGrid.SelectedItems.Cast<object>().ToArray();
        _ = ResultsGrid.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                ResultsGrid.SelectionChanged -= OnResultsGridSelectionChanged;
                try
                {
                    ResultsGrid.SelectedItems.Clear();
                    foreach (var item in selected)
                    {
                        ResultsGrid.SelectedItems.Add(item);
                    }
                }
                finally
                {
                    ResultsGrid.SelectionChanged += OnResultsGridSelectionChanged;
                    UpdateSelectionSnapshot();
                }
            }));
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? source)
        where T : System.Windows.DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : System.Windows.LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnResultsGridSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateSelectionSnapshot();

    private async void OnResultsGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _pointerContextItem = null;
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, source)
                is not System.Windows.Controls.DataGridRow row)
        {
            return;
        }

        _pointerContextItem = row.Item as CatalogItemViewModel;
        if (_pointerContextItem?.IsPlaceholder != false) { _pointerContextItem = null; return; }
        if (_viewModel.UsesQuerySelection)
        {
            ResultsGrid.CurrentItem = row.Item;
            row.Focus();
            if (!_pointerContextItem.IsSelected) await _viewModel.SelectRowAsync(_pointerContextItem, additive: false, range: false);
            return;
        }

        if (!row.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear();
            row.IsSelected = true;
            ResultsGrid.SelectedItem = row.Item;
        }

        // Keep an existing multi-selection intact. Context-menu actions receive
        // CurrentItem, while selection-wide actions continue to use SelectedItems.
        ResultsGrid.CurrentItem = row.Item;
        row.Focus();
    }

    private void OnResultsGridContextMenuOpening(
        object sender,
        System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0)
        {
            // Keyboard invocation uses the DataGrid's current row.
            e.Handled = ResultsGrid.CurrentItem is not CatalogItemViewModel { IsPlaceholder: false };
            return;
        }

        // The menu is row-specific. Do not reuse the previous CurrentItem when
        // the pointer is over a header, scrollbar, or empty grid area.
        e.Handled = _pointerContextItem is null;
    }

    private void UpdateSelectionSnapshot()
    {
        if (_viewModel.UsesQuerySelection) return;
        _viewModel.SetSelection(ResultsGrid.SelectedItems.OfType<CatalogItemViewModel>());
    }

    private void OnResultsGridSorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        if (!_viewModel.UsesQuerySelection) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? FindEverything.Application.Indexing.EntrySortDirection.Descending
            : FindEverything.Application.Indexing.EntrySortDirection.Ascending;
        foreach (var column in ResultsGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction == FindEverything.Application.Indexing.EntrySortDirection.Ascending
            ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending;
        _viewModel.ApplySort(e.Column.SortMemberPath, direction);
    }
}
