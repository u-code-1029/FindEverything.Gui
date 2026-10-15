using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Call only from the existing composition smoke application's UI dispatcher.</summary>
internal static class ScanConsoleUiRegressionChecks
{
    internal static void Verify()
    {
        using var viewModel = new ScanConsoleViewModel(static _ => { }) { IsPanelOpen = true, AutoScroll = false };
        var panel = new ScanConsolePanel { DataContext = viewModel };
        var application = System.Windows.Application.Current;
        var originalMainWindow = application?.MainWindow;
        var window = new Window { Content = panel, Width = 900, Height = 320, ShowInTaskbar = false };
        try
        {
            window.Show();
            Flush();
            var list = Assert.IsType<ListBox>(panel.FindName("TraceList"));
            var viewer = FindScrollViewer(list)!;
            Assert.NotNull(viewer);
            Assert.Equal(ScrollUnit.Item, VirtualizingPanel.GetScrollUnit(list));
            var trace = new CatalogScanTraceEvent(Guid.NewGuid(), 0, DateTimeOffset.UtcNow,
                CatalogScanTraceKind.Started, "console-performance", "Console", @"C:\Root", null, null, null,
                null, null, new Dictionary<string, object?>(), [], null, "start");
            viewModel.Report(trace);
            Append(viewModel, trace, 1, 5_000);
            viewModel.DrainPendingLines();
            Flush();
            viewer.ScrollToVerticalOffset(4_500);
            viewer.ScrollToHorizontalOffset(80);
            Flush();
            var offset = viewer.VerticalOffset;
            var horizontal = viewer.HorizontalOffset;
            var anchor = viewModel.Lines[(int)Math.Floor(offset)];
            var selected = viewModel.Lines[(int)Math.Floor(offset) + 1];
            viewModel.SelectedLine = selected;
            var notifications = 0;
            viewModel.Lines.CollectionChanged += (_, _) => notifications++;

            Append(viewModel, trace, 5_001, 5_500);
            viewModel.DrainPendingLines();
            Flush();
            Assert.Equal(1, notifications);
            Assert.Same(selected, viewModel.SelectedLine);
            Assert.Same(selected, list.SelectedItem);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - offset), 0, 1);
            Assert.InRange(Math.Abs(viewer.HorizontalOffset - horizontal), 0, 1);

            // Trimming changes the row index but must keep the same first visible
            // row when it is still inside the retained 20k window.
            Append(viewModel, trace, 5_501, 22_500);
            viewModel.DrainPendingLines();
            Flush();
            Assert.Equal(2, notifications);
            var newIndex = viewModel.Lines.IndexOf(anchor);
            Assert.True(newIndex >= 0);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - newIndex), 0, 1);
            Assert.Same(selected, viewModel.SelectedLine);
            Assert.Same(selected, list.SelectedItem);

            // Closing/opening takes a new snapshot; no more rows should be
            // published while hidden and the paused anchor should survive.
            panel.Visibility = Visibility.Collapsed;
            viewModel.IsPanelOpen = false;
            Append(viewModel, trace, 22_501, 22_600);
            viewModel.DrainPendingLines();
            Assert.Equal(2, notifications);
            viewModel.IsPanelOpen = true;
            panel.Visibility = Visibility.Visible;
            Flush();
            Assert.Equal(3, notifications);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - viewModel.Lines.IndexOf(anchor)), 0, 1);
            Assert.Same(selected, list.SelectedItem);
        }
        finally
        {
            window.Close();
            if (application is not null) application.MainWindow = originalMainWindow;
        }
    }

    private static void Append(ScanConsoleViewModel viewModel, CatalogScanTraceEvent trace, int first, int last)
    {
        for (var index = first; index <= last; index++)
            viewModel.Report(trace with
            {
                Kind = CatalogScanTraceKind.DirectoryVisited, Sequence = index,
                FullPath = $@"C:\Root\{index}", MatchInput = $@"C:\Root\{index}" + new string('x', 300),
                MappingStatus = ProfileMapStatus.Success, TraversalDecision = DirectoryTraversalDecision.Continue,
            });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
