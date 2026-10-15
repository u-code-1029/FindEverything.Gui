using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

internal static class ScanConsoleCompactUiChecks
{
    internal static void Verify()
    {
        string? clipboard = null;
        using var model = new ScanConsoleViewModel(value => clipboard = value) { IsPanelOpen = true, AutoScroll = false };
        var panel = new ScanConsolePanel { DataContext = model, Width = 660, Height = 164 };
        var application = System.Windows.Application.Current;
        var originalMainWindow = application.MainWindow;
        var window = new Window { Content = panel, Width = 700, Height = 220, ShowInTaskbar = false };
        try
        {
            window.Show(); Flush();
            var layout = Assert.IsType<Grid>(panel.FindName("ScanConsoleLayout"));
            Assert.Equal(3, layout.RowDefinitions.Count);
            Assert.Equal(44, layout.RowDefinitions[0].Height.Value);
            Assert.True(layout.RowDefinitions[1].Height.IsStar);
            Assert.Equal(28, layout.RowDefinitions[2].Height.Value);
            var trace = Assert.IsType<ListBox>(panel.FindName("TraceList"));
            Assert.True(trace.ActualHeight >= 72, $"Compact log viewport is too small: {trace.ActualHeight}.");
            var height = trace.ActualHeight;
            foreach (var name in new[] { "CopyAllLogsButton", "SaveScanLogButton", "ClearLogsButton" })
            {
                var button = Assert.IsType<Wpf.Ui.Controls.Button>(panel.FindName(name));
                Assert.Null(button.Content);
                Assert.Equal(32, button.Width);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.NotNull(button.ToolTip);
            }
            model.SessionSummary = new string('W', 1000);
            model.StatusText = new string('W', 500);
            model.RetentionSummary = new string('W', 500);
            model.VisitedCount = model.MatchedCount = model.NoMatchCount = model.InvalidCount = model.PrunedCount = long.MaxValue;
            Flush();
            Assert.Equal(height, trace.ActualHeight);
            Assert.Equal(TextWrapping.NoWrap, Assert.IsAssignableFrom<TextBlock>(panel.FindName("LogCountersText")).TextWrapping);

            var row = new ScanConsoleLineViewModel(Guid.NewGuid(), 1, CatalogScanTraceKind.DirectoryVisited,
                ScanConsoleLineTone.Match, "[MATCH] complete message", "session", @"C:\Archive\item");
            model.Lines.Add(row); model.SelectedLine = row; Flush();
            Assert.True(ApplicationCommands.Copy.CanExecute(null, trace));
            ApplicationCommands.Copy.Execute(null, trace);
            Assert.Equal(row.Text, clipboard);
            Assert.True(ScanConsolePanel.CopyLogPathCommand.CanExecute(null, trace));
            ScanConsolePanel.CopyLogPathCommand.Execute(null, trace);
            Assert.Equal(row.Path, clipboard);
            Assert.Contains(trace.InputBindings.OfType<KeyBinding>(), binding => binding.Command == ScanConsolePanel.CopyLogPathCommand
                && binding.Key == Key.C && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            model.SelectedLine = null;
            Assert.False(ApplicationCommands.Copy.CanExecute(null, trace));

            panel.Width = 1000; window.Width = 1040; Flush();
            Assert.NotNull(Assert.IsType<Wpf.Ui.Controls.Button>(panel.FindName("SaveScanLogButton")).Content);
            Assert.Equal(height, trace.ActualHeight);
        }
        finally { window.Close(); application.MainWindow = originalMainWindow; }
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
