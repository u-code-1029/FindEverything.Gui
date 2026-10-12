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
    private PendingRowClick? _pendingRowClick;
    private bool? _selectionCompactMode;

    private sealed record PendingRowClick(CatalogItemViewModel Item, System.Windows.Point Start,
        bool Range, int ClickCount);

    public CatalogPage(
        CatalogViewModel viewModel,
        ScanConsoleViewModel scanConsoleViewModel,
        IGridLayoutStore gridLayoutStore)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ResultsGrid.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnResultsGridPreviewMouseLeftButtonUp), handledEventsToo: true);
        ResultsGrid.AddHandler(System.Windows.UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnResultsGridPreviewMouseMove), handledEventsToo: true);
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
        var compact = CatalogPageContent.ActualWidth < 900;
        if (compact)
        {
            CopySelectionButton.Content = null;
            ExportSelectionButton.Content = null;
        }
        else
        {
            CopySelectionButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Loc.Workspace.Selection.Copy");
            ExportSelectionButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Loc.Workspace.Selection.Export");
        }
        CopySelectionButton.Width = ExportSelectionButton.Width = compact ? 32 : double.NaN;
        if (_selectionCompactMode != compact)
        {
            _selectionCompactMode = compact;
            CatalogOutputFormatComboBox.Width = compact ? 80 : 140;
            CatalogOutputFormatLabel.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty,
                compact ? "Loc.Workspace.Output.Format.ShortLabel" : "Loc.Workspace.Output.Format.Label");
            foreach (var (button, symbol) in new[]
            {
                (SelectAllResultsButton, Wpf.Ui.Controls.SymbolRegular.CheckboxChecked20),
                (ClearAllResultsButton, Wpf.Ui.Controls.SymbolRegular.CheckboxUnchecked20),
                (InvertResultsSelectionButton, Wpf.Ui.Controls.SymbolRegular.ArrowSwap20),
            })
            {
                // Keep all action words visible; omit redundant icons in a narrow footer.
                button.Icon = compact ? null : new Wpf.Ui.Controls.SymbolIcon(symbol);
                button.Padding = new System.Windows.Thickness(compact ? 4 : 8, 4, compact ? 4 : 8, 4);
                button.FontSize = compact ? 13 : 14;
            }
        }
    }

    private async void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultsGrid.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.DependencyObject focused
            && !ResultGridInput.IsInteractiveOrigin(focused)
            && ResultsGrid.CurrentItem is CatalogItemViewModel { IsPlaceholder: false } current)
        {
            if (e.Key == Key.Space)
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

        if (item.IsPlaceholder) return;
        ResultsGrid.CurrentItem = item;
        _viewModel.SelectedItem = item;
        if (_viewModel.OpenItemCommand.CanExecute(item))
        {
            _viewModel.OpenItemCommand.Execute(item);
        }
    }

    private void OnResultsGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pendingRowClick = null;
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || ResultGridInput.IsInteractiveOrigin(source)
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, source)
                is not System.Windows.Controls.DataGridRow row)
        {
            return;
        }

        // Grid focus is independent of the checkbox-backed multi-selection.
        // Delay the toggle until release so dragging or the second click of a
        // folder-opening double-click cannot undo an already checked item.
        e.Handled = true;
        if (row.Item is not CatalogItemViewModel { IsPlaceholder: false } item) return;
        ResultsGrid.CurrentItem = item;
        _viewModel.SelectedItem = item;
        row.Focus();
        if (e.ClickCount == 1)
            _pendingRowClick = new(item, e.GetPosition(ResultsGrid),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), e.ClickCount);
    }

    private async void OnResultsGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pending = _pendingRowClick;
        _pendingRowClick = null;
        if (pending is null || e.OriginalSource is not System.Windows.DependencyObject source
            || ResultGridInput.IsInteractiveOrigin(source)
            || !ResultGridInput.IsClickGesture(pending.Start, e.GetPosition(ResultsGrid), pending.ClickCount)
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, source)
                is not System.Windows.Controls.DataGridRow { Item: CatalogItemViewModel item }
            || !ReferenceEquals(item, pending.Item)) return;

        e.Handled = true;
        await _viewModel.SelectRowAsync(item, additive: true, range: pending.Range);
    }

    private void OnResultsGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingRowClick is { } pending
            && !ResultGridInput.IsClickGesture(pending.Start, e.GetPosition(ResultsGrid), pending.ClickCount))
            _pendingRowClick = null;
    }

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
        // Keep an existing multi-selection intact. Context-menu actions receive
        // CurrentItem, while selection-wide actions use the checked models.
        ResultsGrid.CurrentItem = row.Item;
        _viewModel.SelectedItem = _pointerContextItem;
        row.Focus();
        if (!_pointerContextItem.IsSelected)
            await _viewModel.SelectRowAsync(_pointerContextItem, additive: true, range: false);
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
