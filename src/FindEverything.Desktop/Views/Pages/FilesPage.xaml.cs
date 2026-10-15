using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Behaviors;

namespace FindEverything.Desktop.Views.Pages;

public partial class FilesPage
{
    private readonly FileSearchViewModel _viewModel;
    private FileSearchItemViewModel? _pointerContextItem;
    private bool? _selectionCompactMode;

    public FilesPage(FileSearchViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        PageActivation.Attach(this);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileSearchViewModel.IsDetailsOpen)) ApplyWorkspaceLayout();
            if (e.PropertyName == nameof(FileSearchViewModel.IsBusy) && viewModel.IsBusy)
            {
                if (ResultsGrid.ContextMenu is { } menu) menu.IsOpen = false;
                if (SelectionActionsMenuButton.ContextMenu is { } selectionMenu) selectionMenu.IsOpen = false;
                if (CopySelectionButton.Flyout is ContextMenu formatMenu) formatMenu.IsOpen = false;
            }
        };
    }

    private void OnWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (FileResultsSurface is null || FileDetailsPanel is null) return;
        var reserveDetails = _viewModel.IsDetailsOpen && FilePageLayout.ActualWidth >= 900;
        FileResultsSurface.Margin = new System.Windows.Thickness(0, 0, reserveDetails ? 348 : 0, 0);
        FileDetailsPanel.Width = Math.Min(340, Math.Max(200, FilePageLayout.ActualWidth));
        ApplySelectionToolbarLayout();
    }

    private void OnSelectionToolbarSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplySelectionToolbarLayout();

    private void ApplySelectionToolbarLayout()
    {
        if (SelectionToolbar is null || SelectionToolbar.ActualWidth <= 0) return;
        var compact = SelectionToolbar.ActualWidth < 1040;
        if (_selectionCompactMode == compact) return;
        _selectionCompactMode = compact;
        var full = compact ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        var menu = compact ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        SelectAllResultsButton.Visibility = ClearAllResultsButton.Visibility = InvertResultsSelectionButton.Visibility = full;
        SelectionActionsMenuButton.Visibility = menu;
        CopySelectionButton.SetResourceReference(ContentControl.ContentProperty,
            compact ? "Loc.View.Files.Content.Copy" : "Loc.Workspace.Selection.Copy");
    }

    private void OnFooterMenuClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnSelectionCheckBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || ItemsControl.ContainerFromElement(ResultsGrid, checkBox) is not DataGridRow row) return;
        if (_viewModel.UsesQuerySelection)
        {
            e.Handled = true;
            if (row.Item is not FileSearchItemViewModel { IsPlaceholder: false } item) return;
            checkBox.Focus();
            await _viewModel.SelectRowAsync(item, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }
        var selected = !row.IsSelected;
        checkBox.Focus();
        if (selected) row.IsSelected = true;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => row.IsSelected = selected));
        e.Handled = true;
    }

    private async void OnResultsGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.UsesQuerySelection || e.OriginalSource is not System.Windows.DependencyObject source
            || ResultGridInput.IsInteractiveOrigin(source)
            || ItemsControl.ContainerFromElement(ResultsGrid, source) is not DataGridRow row) return;
        e.Handled = true;
        if (row.Item is not FileSearchItemViewModel { IsPlaceholder: false } item) return;
        ResultsGrid.SelectedItem = item;
        ResultsGrid.CurrentItem = item;
        row.Focus();
        // Opening an item with a double-click must not undo the first click's check.
        if (e.ClickCount != 1) return;
        await _viewModel.SelectRowAsync(item, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private async void OnSelectionCheckBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_viewModel.UsesQuerySelection || sender is not CheckBox checkBox
            || ItemsControl.ContainerFromElement(ResultsGrid, checkBox) is not DataGridRow { Item: FileSearchItemViewModel { IsPlaceholder: false } item }) return;
        e.Handled = true;
        await _viewModel.SelectRowAsync(item, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private async void OnSelectionCheckBoxStateChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_viewModel.UsesQuerySelection || sender is not CheckBox checkBox
            || checkBox.DataContext is not FileSearchItemViewModel { IsPlaceholder: false } item
            || checkBox.IsChecked == item.IsSelected) return;
        await _viewModel.SelectRowAsync(item, additive: true, range: false);
    }

    private async void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CopySelectionButton.IsKeyboardFocusWithin && (e.Key == Key.F4
            || e.SystemKey == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            && CopySelectionButton.Flyout is ContextMenu formatMenu)
        {
            formatMenu.PlacementTarget = CopySelectionButton;
            formatMenu.Placement = PlacementMode.Top;
            formatMenu.IsOpen = true; e.Handled = true; return;
        }
        if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            _viewModel.ApplySearchImmediately();
            return;
        }
        if (ResultsGrid.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.DependencyObject focused
            && !ResultGridInput.IsInteractiveOrigin(focused)
            && ResultsGrid.CurrentItem is FileSearchItemViewModel { IsPlaceholder: false } current)
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
            && Keyboard.FocusedElement is not TextBoxBase && Keyboard.FocusedElement is not PasswordBox)
        {
            _viewModel.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.FocusedElement is not TextBoxBase
            && Keyboard.FocusedElement is not PasswordBox
            && _viewModel.CopySelectionCommand.CanExecute(null))
        {
            _viewModel.CopySelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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

    private void OnResultsGridSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionSnapshot();

    private async void OnResultsGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _pointerContextItem = null;
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsGrid, source) is not DataGridRow row)
        {
            return;
        }

        _pointerContextItem = row.Item as FileSearchItemViewModel;
        if (_pointerContextItem?.IsPlaceholder != false) { _pointerContextItem = null; return; }
        if (_viewModel.UsesQuerySelection)
        {
            ResultsGrid.CurrentItem = row.Item;
            row.Focus();
            if (!_pointerContextItem.IsSelected) await _viewModel.SelectRowAsync(_pointerContextItem, additive: true, range: false);
            return;
        }

        if (!row.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear();
            row.IsSelected = true;
            ResultsGrid.SelectedItem = row.Item;
        }

        // Do not replace SelectedItem for a row that already belongs to a
        // multi-selection. Context-menu item actions use CurrentItem instead.
        ResultsGrid.CurrentItem = row.Item;
        row.Focus();
    }

    private void OnResultsGridContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0)
        {
            e.Handled = ResultsGrid.CurrentItem is not FileSearchItemViewModel { IsPlaceholder: false };
            return;
        }

        e.Handled = _pointerContextItem is null;
    }

    private void UpdateSelectionSnapshot()
    {
        if (_viewModel.UsesQuerySelection) return;
        _viewModel.SetSelection(ResultsGrid.SelectedItems.OfType<FileSearchItemViewModel>());
    }

    private void OnResultsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || ResultGridInput.IsInteractiveOrigin(source)
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
