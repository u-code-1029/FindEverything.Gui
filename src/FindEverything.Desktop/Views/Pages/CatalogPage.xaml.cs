using System.Windows.Input;
using System.Windows.Controls.Primitives;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Views.Controls;
using Wpf.Ui;

namespace FindEverything.Desktop.Views.Pages;

public partial class CatalogPage
{
    private readonly CatalogViewModel _viewModel;
    private CatalogItemViewModel? _pointerContextItem;
    private PendingRowClick? _pendingRowClick;
    private bool? _selectionCompactMode;
    private readonly IContentDialogService? _dialogs;
    private readonly ITextFileEditingService _textFiles;
    private readonly LiveDirectoryBrowser _directoryBrowser;
    private CancellationTokenSource? _fileEditCancellation;
    private bool _fileEditorOpen;
    private double _preferredConsoleHeight = 220;
    private bool _consoleResized;
    private System.Windows.UIElement? _lastFocusTarget;

    private sealed record PendingRowClick(CatalogItemViewModel Item, System.Windows.Point Start,
        bool Range, int ClickCount);

    public CatalogPage(
        CatalogViewModel viewModel,
        ScanConsoleViewModel scanConsoleViewModel,
        IGridLayoutStore gridLayoutStore,
        IProfilePresentationStore? presentationStore = null,
        IContentDialogService? dialogs = null,
        ITextFileEditingService? textFiles = null,
        ILiveDirectoryService? liveDirectories = null,
        IProfilePlaygroundNavigator? playgroundNavigator = null)
    {
        _viewModel = viewModel;
        _playgroundNavigator = playgroundNavigator;
        DataContext = viewModel;
        InitializeComponent();
        GotKeyboardFocus += (_, _) =>
        {
            if (ResultsGrid.IsKeyboardFocusWithin) _lastFocusTarget = ResultsGrid;
            else if (Keyboard.FocusedElement is System.Windows.UIElement element) _lastFocusTarget = element;
        };
        PageActivation.Attach(this);
        _dialogs = dialogs;
        _textFiles = textFiles ?? new TextFileEditingService();
        _directoryBrowser = new LiveDirectoryBrowser(liveDirectories ?? new LiveDirectoryService(), viewModel.Localizer);
        LiveFolderFlyout.Content = _directoryBrowser;
        LiveFolderFlyout.Closed += (_, _) => _directoryBrowser.Stop();
        ResultsGrid.AddHandler(DynamicProfileGrid.BrowseFolderRequestedEvent, new System.Windows.RoutedEventHandler(OnBrowseFolderRequested));
        ResultsGrid.AddHandler(DynamicProfileGrid.EditTextFileRequestedEvent, new System.Windows.RoutedEventHandler(OnEditTextFileRequested));
        ResultsGrid.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnResultsGridPreviewMouseLeftButtonUp), handledEventsToo: true);
        ResultsGrid.AddHandler(System.Windows.UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnResultsGridPreviewMouseMove), handledEventsToo: true);
        ScanConsolePanel.DataContext = scanConsoleViewModel;
        DynamicProfileGrid.SetLayoutStore(ResultsGrid, gridLayoutStore);
        DynamicProfileGrid.SetPresentationStore(ResultsGrid, presentationStore);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogViewModel.IsDetailsOpen))
            {
                ApplyWorkspaceLayout();
                if (viewModel.IsDetailsOpen) QueueDetailsFocus(CloseCatalogDetailsButton, () => viewModel.IsDetailsOpen);
            }
            if (e.PropertyName == nameof(CatalogViewModel.IsScanning) && viewModel.IsScanning)
            {
                _pendingRowClick = null;
                LiveFolderFlyout.Hide(); _directoryBrowser.Stop(); _fileEditCancellation?.Cancel();
                if (ResultsGrid.ContextMenu is { } menu) menu.IsOpen = false;
                if (SelectionActionsMenuButton.ContextMenu is { } selectionMenu) selectionMenu.IsOpen = false;
                if (CopySelectionButton.Flyout is System.Windows.Controls.ContextMenu formatMenu) formatMenu.IsOpen = false;
            }
            if (e.PropertyName is nameof(CatalogViewModel.SelectedProfile) or nameof(CatalogViewModel.RootPath) or nameof(CatalogViewModel.IsReadOnlyDataSource))
            { LiveFolderFlyout.Hide(); _directoryBrowser.Stop(); _fileEditCancellation?.Cancel(); }
        };
    }

    private async void OnBrowseFolderRequested(object sender, System.Windows.RoutedEventArgs e)
    {
        if (e is not DynamicProfileGrid.ProfileCellActionEventArgs action || action.Row.IsPlaceholder
            || e.OriginalSource is not System.Windows.FrameworkElement target || !IsLoaded || !_viewModel.IsResultInteractionEnabled) return;
        e.Handled = true;
        LiveFolderFlyout.Hide(); _directoryBrowser.Stop();
        LiveFolderFlyout.ApplyTemplate();
        if (LiveFolderFlyout.Template?.FindName("PART_Popup", LiveFolderFlyout) is not System.Windows.Controls.Primitives.Popup popup) return;
        popup.PlacementTarget = target; popup.StaysOpen = false;
        _directoryBrowser.Width = Math.Min(420, Math.Max(280, ActualWidth - 48));
        LiveFolderFlyout.Show();
        try { await _directoryBrowser.OpenAsync(action.Row.FullPath); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _viewModel.ShowTextFileEditError(exception); }
    }

    private async void OnEditTextFileRequested(object sender, System.Windows.RoutedEventArgs e)
    {
        if (e is not DynamicProfileGrid.ProfileCellActionEventArgs { FieldId: { } fieldId } action
            || _fileEditorOpen || _dialogs is null || !IsLoaded || !_viewModel.IsResultInteractionEnabled) return;
        e.Handled = true; _fileEditorOpen = true;
        using var cancellation = new CancellationTokenSource(); _fileEditCancellation = cancellation;
        try
        {
            var context = _viewModel.CaptureTextFileEditContext(action.Row, fieldId);
            var document = await _textFiles.OpenAsync(action.Row.FullPath, context.Field, context.Resource,
                action.Row.CaptureFileNameValues(), cancellation.Token);
            _viewModel.ValidateTextFileEditContext(context); cancellation.Token.ThrowIfCancellationRequested();
            var dialog = new TextFileEditorDialog(document, _viewModel.Localizer, async content =>
            {
                _viewModel.ValidateTextFileEditContext(context); cancellation.Token.ThrowIfCancellationRequested();
                var cacheUpdated = await _viewModel.SaveTextFileEditAsync(context,
                    token => _textFiles.SaveAsync(document, content, token), cancellation.Token);
                _viewModel.ShowTextFileEditResult(document.FilePath, cacheUpdated);
                return true;
            });
            await _dialogs.ShowAsync(dialog, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { _viewModel.ShowTextFileEditError(exception); }
        finally { if (ReferenceEquals(_fileEditCancellation, cancellation)) _fileEditCancellation = null; _fileEditorOpen = false; }
    }

    private void OnWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ScanConsolePanel is null || CatalogResultsWorkspace is null) return;
        // Keep the search and profile/location rows intact at every supported
        // width. Secondary labels shorten to accessible icon buttons instead of
        // wrapping the toolbar or making progress push the result grid around.
        var labelVisibility = CatalogPageContent.ActualWidth >= 950
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        CatalogDataManagementLabel.Visibility = SelectFromDataLabel.Visibility = labelVisibility;
        var maximumConsoleHeight = Math.Max(164, CatalogPageLayout.ActualHeight - 264);
        ScanConsolePanel.MaxHeight = maximumConsoleHeight;
        var desiredConsoleHeight = _consoleResized ? _preferredConsoleHeight
            : Math.Min(_preferredConsoleHeight, CatalogPageLayout.ActualHeight * 0.3);
        ScanConsolePanel.Height = Math.Clamp(desiredConsoleHeight, 164, maximumConsoleHeight);
        var reserveDetails = _viewModel.IsDetailsOpen && CatalogPageContent.ActualWidth >= 900;
        CatalogResultsWorkspace.Margin = new System.Windows.Thickness(0, 0, reserveDetails ? 348 : 0, 0);
        CatalogDetailsPanel.Width = Math.Min(340, Math.Max(200, CatalogPageContent.ActualWidth));
        ApplySelectionToolbarLayout();
    }

    private void OnSelectionToolbarSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplySelectionToolbarLayout();

    private void OnConsoleSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        _consoleResized = true;
        _preferredConsoleHeight = Math.Clamp(ScanConsolePanel.ActualHeight - e.VerticalChange, 164, ScanConsolePanel.MaxHeight);
        ApplyWorkspaceLayout();
    }

    private void OnConsoleSplitterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        _consoleResized = true;
        _preferredConsoleHeight = Math.Clamp(ScanConsolePanel.ActualHeight + (e.Key == Key.Up ? 20 : -20), 164, ScanConsolePanel.MaxHeight);
        ApplyWorkspaceLayout(); e.Handled = true;
    }

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
    }

    private void QueueDetailsFocus(System.Windows.UIElement target, Func<bool> stillRelevant) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (IsLoaded && stillRelevant() && target.IsVisible && target.IsEnabled) target.Focus();
        }));

    private void OnCloseDetailsClick(object sender, System.Windows.RoutedEventArgs e) =>
        QueueDetailsFocus(CatalogDataManagementButton, () => !_viewModel.IsDetailsOpen);

    private void OnWorkspacePaneKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || !_viewModel.IsDetailsOpen || _fileEditorOpen) return;
        _viewModel.CloseDetailsCommand.Execute(null);
        QueueDetailsFocus(CatalogDataManagementButton, () => !_viewModel.IsDetailsOpen);
        e.Handled = true;
    }

    private void OnFooterMenuClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var selectionScope = ResultsGrid.IsKeyboardFocusWithin || SelectionToolbar.IsKeyboardFocusWithin;
        if (CopySelectionButton.IsKeyboardFocusWithin && (e.Key == Key.F4
            || e.SystemKey == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            && CopySelectionButton.Flyout is System.Windows.Controls.ContextMenu formatMenu)
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
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && selectionScope
            && Keyboard.FocusedElement is not TextBoxBase && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox
            && _viewModel.CanOpenSelectionImport)
        {
            e.Handled = true; await _viewModel.OpenSelectionImportFromClipboardAsync(); return;
        }
        // Ordinary row clicks now focus the selector. Retain Enter-to-open
        // without intercepting the checkbox's own Space/range selection input.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is System.Windows.Controls.CheckBox selector
            && System.Windows.Controls.ItemsControl.ContainerFromElement(ResultsGrid, selector)
                is System.Windows.Controls.DataGridRow { Item: CatalogItemViewModel { IsPlaceholder: false } selected }
            && _viewModel.OpenItemCommand.CanExecute(selected))
        {
            e.Handled = true;
            _viewModel.OpenItemCommand.Execute(selected);
            return;
        }
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
            && selectionScope
            && Keyboard.FocusedElement is not TextBoxBase && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox)
        {
            _viewModel.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C
            && selectionScope
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

    private void OnPageLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        ApplyWorkspaceLayout();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !IsVisible || IsKeyboardFocusWithin) return;
            var target = _lastFocusTarget is { IsVisible: true, IsEnabled: true } previous ? previous : SearchBox;
            if (target.IsEnabled) target.Focus();
        }));
    }

    private void OnPageUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        LiveFolderFlyout.Hide(); _directoryBrowser.Stop(); _fileEditCancellation?.Cancel();
    }

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
        DynamicProfileGrid.FocusSelectionCheckBox(row);
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
        if (ResultGridInput.IsInteractiveOrigin(source)) return;
        row.Focus();
        if (!_pointerContextItem.IsSelected)
            await _viewModel.SelectRowAsync(_pointerContextItem, additive: true, range: false);
    }

    private void OnResultsGridContextMenuOpening(
        object sender,
        System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source && ResultGridInput.IsInteractiveOrigin(source)) return;
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
