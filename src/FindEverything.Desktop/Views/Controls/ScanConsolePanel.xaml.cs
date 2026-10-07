using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Controls;

public partial class ScanConsolePanel
{
    private ScanConsoleViewModel? _viewModel;
    private bool _scrollScheduled;

    public ScanConsolePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
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
            ScheduleScrollToEnd();
        }
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
            _viewModel.Lines.CollectionChanged += OnLinesCollectionChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged -= OnLinesCollectionChanged;
            _viewModel = null;
        }
    }

    private void OnLinesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        ScheduleScrollToEnd();
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
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
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
