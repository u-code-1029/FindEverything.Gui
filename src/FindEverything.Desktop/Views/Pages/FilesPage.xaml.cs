using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;

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
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileSearchViewModel.IsDetailsOpen)) ApplyWorkspaceLayout();
        };
    }

    private void OnWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ResultsGrid is null || FileDetailsPanel is null) return;
        var reserveDetails = _viewModel.IsDetailsOpen && FilePageLayout.ActualWidth >= 900;
        ResultsGrid.Margin = new System.Windows.Thickness(0, 0, reserveDetails ? 348 : 0, 0);
        FileDetailsPanel.Width = Math.Min(340, Math.Max(200, FilePageLayout.ActualWidth));
        var compact = FilePageLayout.ActualWidth < 900;
        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, compact ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, compact ? 2 : 1);
        SearchBox.Margin = compact ? new System.Windows.Thickness(0) : new System.Windows.Thickness(12, 0, 0, 0);
        Grid.SetRow(FileRootPathText, compact ? 1 : 0);
        Grid.SetColumnSpan(FileRootPathText, compact ? 3 : 1);
        FileRootPathText.Margin = compact ? new System.Windows.Thickness(0, 4, 0, 0) : new System.Windows.Thickness(0);
        FileRootPathText.FontSize = compact ? 12 : 14;
        FileLocationColumn.Width = compact ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) : new System.Windows.GridLength(220);
        FileSearchColumn.Width = compact ? new System.Windows.GridLength(0) : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        if (compact) CopySelectionButton.Content = null;
        else CopySelectionButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Workspace.Selection.Copy");
        CopySelectionButton.Width = compact ? 32 : double.NaN;
        if (_selectionCompactMode != compact)
        {
            _selectionCompactMode = compact;
            FilesOutputFormatComboBox.Width = compact ? 80 : 140;
            FilesOutputFormatLabel.SetResourceReference(ContentControl.ContentProperty,
                compact ? "Loc.Workspace.Output.Format.ShortLabel" : "Loc.Workspace.Output.Format.Label");
            foreach (var (button, symbol) in new[]
            {
                (SelectAllResultsButton, Wpf.Ui.Controls.SymbolRegular.CheckboxChecked20),
                (ClearAllResultsButton, Wpf.Ui.Controls.SymbolRegular.CheckboxUnchecked20),
                (InvertResultsSelectionButton, Wpf.Ui.Controls.SymbolRegular.ArrowSwap20),
            })
            {
                button.Icon = compact ? null : new Wpf.Ui.Controls.SymbolIcon(symbol);
                button.Padding = new System.Windows.Thickness(compact ? 4 : 8, 4, compact ? 4 : 8, 4);
                button.FontSize = compact ? 13 : 14;
            }
        }
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
        await _viewModel.SelectRowAsync(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
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
            if (!_pointerContextItem.IsSelected) await _viewModel.SelectRowAsync(_pointerContextItem, additive: false, range: false);
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
