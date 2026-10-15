using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

internal static class GridLayoutImportUiChecks
{
    internal static void Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), "layout-import-ui-" + Guid.NewGuid().ToString("N"));
        var store = new GridLayoutStore(new(directory, Path.Combine(directory, "settings.json")), NullLogger<GridLayoutStore>.Instance);
        var fields = Enumerable.Range(0, 3).Select(index => new ProfileFieldDescriptor($"f{index}", $"f{index}", $"Field {index}",
            index, false, ProfileFieldValueKind.String, true, null, null)).ToArray();
        var grid = new DataGrid { AutoGenerateColumns = false, Width = 900, Height = 300 };
        DynamicProfileGrid.SetLayoutStore(grid, store);
        DynamicProfileGrid.SetProfileId(grid, "profile");
        DynamicProfileGrid.SetFields(grid, fields);
        var application = System.Windows.Application.Current;
        var originalMainWindow = application.MainWindow;
        var window = new Window { Content = grid, Width = 940, Height = 350, ShowInTaskbar = false };
        try
        {
            window.Show(); Flush();
            var sameColumn = grid.Columns[3];
            var imported = new GridColumnLayout("f1", 1, 410, GridColumnWidthMode.Manual, true);
            store.SaveAsync("profile", [imported]).GetAwaiter().GetResult();
            // A user action can already be queued when a background import commits.
            typeof(DynamicProfileGrid).GetMethod("SaveLayout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [grid]);
            Assert.Equal(imported, store.Get("profile")["f1"]);
            Flush();
            DynamicProfileGrid.SetFields(grid, fields.ToArray());
            Assert.Same(sameColumn, grid.Columns[3]);
            Assert.Equal(Visibility.Collapsed, sameColumn.Visibility);
            Assert.Equal(410, sameColumn.Width.Value);
            // Subsequent edits must preserve the newly imported hidden state.
            typeof(DynamicProfileGrid).GetMethod("SaveLayout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [grid]);
            Assert.True(store.Get("profile")["f1"].IsHidden);
        }
        finally
        {
            window.Close(); Flush();
            application.MainWindow = originalMainWindow;
            store.StopAsync(default).GetAwaiter().GetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
