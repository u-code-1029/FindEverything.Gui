using System.Collections.Specialized;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views;

public partial class ScanConsoleWindow
{
    private readonly ScanConsoleViewModel _viewModel;
    private bool _scrollScheduled;

    public ScanConsoleWindow(ScanConsoleViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Lines.CollectionChanged += OnLinesCollectionChanged;
        Closed += OnClosed;
    }

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (!_viewModel.AutoScroll || _scrollScheduled || _viewModel.Lines.Count == 0)
        {
            return;
        }

        _scrollScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                _scrollScheduled = false;
                if (_viewModel.AutoScroll && _viewModel.Lines.Count > 0)
                {
                    TraceList.ScrollIntoView(_viewModel.Lines[^1]);
                }
            });
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Closed -= OnClosed;
        _viewModel.Lines.CollectionChanged -= OnLinesCollectionChanged;
    }
}
