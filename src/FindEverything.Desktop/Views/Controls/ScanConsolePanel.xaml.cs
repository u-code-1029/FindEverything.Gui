using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Controls;

public partial class ScanConsolePanel
{
    public static readonly RoutedUICommand CopyLogPathCommand = new("Copy log path", nameof(CopyLogPathCommand), typeof(ScanConsolePanel));
    private ScanConsoleViewModel? _viewModel;
    private bool _scrollScheduled;
    private ScrollViewer? _traceScrollViewer;
    private ScanConsoleLineViewModel? _pausedAnchor;
    private double _pausedFraction;
    private double _pausedHorizontalOffset;
    private long _attachmentVersion;
    private bool? _compact;

    public ScanConsolePanel()
    {
        InitializeComponent();
        TraceList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyLogExecuted, OnCopyLogCanExecute));
        TraceList.CommandBindings.Add(new CommandBinding(CopyLogPathCommand, OnCopyLogExecuted, OnCopyLogCanExecute));
        TraceList.InputBindings.Add(new KeyBinding(CopyLogPathCommand, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        // Viewport anchors are logical rows, independently of display DPI/font.
        VirtualizingPanel.SetScrollUnit(TraceList, ScrollUnit.Item);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnPanelSizeChanged;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 820;
        if (_compact == compact) return;
        _compact = compact;
        if (compact)
        {
            CopyAllLogsButton.Content = SaveScanLogButton.Content = ClearLogsButton.Content = null;
        }
        else
        {
            CopyAllLogsButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Scan.CopyAll");
            SaveScanLogButton.SetResourceReference(ContentControl.ContentProperty, "Loc.View.ScanConsolePanel.Content.SaveLog");
            ClearLogsButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Scan.Clear");
        }
        foreach (var button in new[] { CopyAllLogsButton, SaveScanLogButton, ClearLogsButton })
        {
            button.MinWidth = compact ? 32 : 84;
            button.Width = compact ? 32 : double.NaN;
            button.Padding = compact ? new Thickness(0) : new Thickness(8, 4, 8, 4);
        }
    }

    private void OnCopyLogCanExecute(object sender, CanExecuteRoutedEventArgs args)
    {
        var model = DataContext as ScanConsoleViewModel;
        args.CanExecute = args.Command == CopyLogPathCommand
            ? model?.CopySelectedPathCommand.CanExecute(null) == true
            : model?.CopySelectedMessageCommand.CanExecute(null) == true;
        args.Handled = true;
    }

    private void OnCopyLogExecuted(object sender, ExecutedRoutedEventArgs args)
    {
        if (DataContext is ScanConsoleViewModel model)
        {
            var command = args.Command == CopyLogPathCommand ? model.CopySelectedPathCommand : model.CopySelectedMessageCommand;
            if (command.CanExecute(null)) command.Execute(null);
        }
        args.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Attach(DataContext as ScanConsoleViewModel);
        ScheduleScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        Detach();
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        Detach();
        if (IsLoaded)
        {
            Attach(eventArgs.NewValue as ScanConsoleViewModel);
            ScheduleScrollToEnd();
        }
    }

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (IsVisible)
        {
            if (_viewModel is { AutoScroll: false }) RestorePausedViewport();
            else ScheduleScrollToEnd();
        }
        else CapturePausedViewport();
    }

    private void Attach(ScanConsoleViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        Detach();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.VisibleLinesChanging += OnVisibleLinesChanging;
            _viewModel.Lines.CollectionChanged += OnLinesCollectionChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.VisibleLinesChanging -= OnVisibleLinesChanging;
            _viewModel.Lines.CollectionChanged -= OnLinesCollectionChanged;
            _viewModel = null;
        }
        _attachmentVersion++;
        _pausedAnchor = null;
        _traceScrollViewer = null;
        _scrollScheduled = false;
    }

    private void OnVisibleLinesChanging(object? sender, EventArgs eventArgs)
    {
        // Opening publishes before the Visibility binding is updated. Keep the
        // anchor captured when closing instead of replacing it while collapsed.
        if (IsVisible) CapturePausedViewport();
    }

    private void CapturePausedViewport()
    {
        if (_viewModel is not { AutoScroll: false } viewModel) return;
        _traceScrollViewer ??= FindScrollViewer(TraceList);
        if (_traceScrollViewer is not { } viewer) return;
        _pausedAnchor = null;
        var index = Math.Max(0, (int)Math.Floor(viewer.VerticalOffset));
        if (index < viewModel.Lines.Count) _pausedAnchor = viewModel.Lines[index];
        _pausedFraction = viewer.VerticalOffset - index;
        _pausedHorizontalOffset = viewer.HorizontalOffset;
    }

    private void OnLinesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_viewModel is { AutoScroll: false }) RestorePausedViewport();
        else ScheduleScrollToEnd();
    }

    private void RestorePausedViewport()
    {
        if (!IsLoaded || !IsVisible || _traceScrollViewer is null) return;
        var version = _attachmentVersion;
        var anchor = _pausedAnchor;
        var fraction = _pausedFraction;
        var horizontal = _pausedHorizontalOffset;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != _attachmentVersion || !IsLoaded || !IsVisible
                || _viewModel is not { AutoScroll: false } viewModel || _traceScrollViewer is not { } viewer) return;
            var index = anchor is null ? 0 : viewModel.Lines.IndexOf(anchor);
            viewer.ScrollToVerticalOffset(Math.Max(0, index) + fraction);
            viewer.ScrollToHorizontalOffset(horizontal);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private void OnTraceListPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not ListBox listBox
            || eventArgs.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            _ = item.Focus();
            return;
        }

        listBox.SelectedItem = null;
    }

    private void ScheduleScrollToEnd()
    {
        if (!IsLoaded
            || !IsVisible
            || _viewModel is not { AutoScroll: true }
            || _scrollScheduled
            || _viewModel.Lines.Count == 0)
        {
            return;
        }

        _scrollScheduled = true;
        var version = _attachmentVersion;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                if (version != _attachmentVersion) return;
                _scrollScheduled = false;
                if (IsLoaded
                    && IsVisible
                    && _viewModel is { AutoScroll: true }
                    && _viewModel.Lines.Count > 0)
                {
                    TraceList.ScrollIntoView(_viewModel.Lines[^1]);
                }
            });
    }
}
