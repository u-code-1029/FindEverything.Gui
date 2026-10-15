using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>All WPF resources are used on the composition smoke application's one dispatcher.</summary>
internal static class GridSizingRegressionChecks
{
    internal static void Verify()
    {
        var fields = Enumerable.Range(0, 6).Select(i => new ProfileFieldDescriptor($"f{i}", $"f{i}", $"Column {i}", i,
            false, ProfileFieldValueKind.String, true, null, null)).ToArray();
        var values = fields.ToDictionary(static f => f.FieldId, static f => (object?)new string('W', 1000));
        var items = new ObservableCollection<CatalogItemViewModel>(Enumerable.Range(0, 210).Select(i => new CatalogItemViewModel(
            new CatalogItem($@"C:\Archive\{i}", $"{i}", "rule", new object(), values, false), fields, new TestAppLocalizer())));
        var presentation = new MemoryPresentationStore();
        var layout = new MemoryLayoutStore();
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = items, Width = 1140, Height = 400 };
        DynamicProfileGrid.SetLayoutStore(grid, layout);
        DynamicProfileGrid.SetProfileId(grid, "profile");
        DynamicProfileGrid.SetPresentationStore(grid, presentation);
        DynamicProfileGrid.SetLocalizer(grid, new TestAppLocalizer());
        DynamicProfileGrid.SetFields(grid, fields);
        var host = new Window { Content = grid, Width = 1200, Height = 450, ShowInTaskbar = false };
        host.Show();
        try
        {
        grid.Measure(new Size(1140, 400));
        grid.Arrange(new Rect(0, 0, 1140, 400));
        Flush();
        Assert.Equal(64, grid.Columns[0].Width.Value);
        Assert.InRange(grid.Columns[2].Width.Value, 72, 180);
        Assert.InRange(grid.Columns[3].Width.Value, 96, 360);
        Assert.InRange(grid.Columns[4].Width.Value, 160, 560);
        Assert.True(grid.Columns[4].Width.Value > grid.Columns[2].Width.Value);
        var firstColumn = grid.Columns[2];
        var initialWidth = firstColumn.Width;
        DynamicProfileGrid.SetFields(grid, fields.ToArray());
        Assert.Same(firstColumn, grid.Columns[2]);
        items.Add(items[0]);
        Flush();
        Assert.Equal(initialWidth, firstColumn.Width);

        layout.Save("profile", [new("f1", 2, 257, GridColumnWidthMode.Manual)]);
        typeof(DynamicProfileGrid).GetMethod("ApplyLayout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [grid]);
        Flush();
        Assert.Equal(257, grid.Columns[3].Width.Value);
        grid.Width = 760;
        grid.Measure(new Size(760, 400)); grid.Arrange(new Rect(0, 0, 760, 400)); Flush();
        Assert.Equal(257, grid.Columns[3].Width.Value);

        // A preset update clears only that field's manual state and uses the new responsive bounds.
        layout.Save("profile", [new("f1", 2, 257, GridColumnWidthMode.Auto)]);
        presentation.Change("f1", ColumnWidthPreset.Narrow);
        Flush();
        Assert.InRange(grid.Columns[3].Width.Value, 72, 180);
        VerifyHiddenColumnsAndRestore(grid, layout);
        VerifyOnlyUtilityColumnsStayFrozen(grid, layout, presentation);
        }
        finally { host.Close(); }
    }

    private static void VerifyHiddenColumnsAndRestore(DataGrid grid, MemoryLayoutStore layout)
    {
        DynamicProfileGrid.ShowColumnsMenu(grid, grid);
        var menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
        try
        {
            var identity = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Column 0"));
            Assert.True(identity.IsChecked);
            Assert.False(identity.IsEnabled);
            var field = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Column 2"));
            field.IsChecked = false;
            field.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, field));
            Flush();
            Assert.Equal(Visibility.Collapsed, grid.Columns[4].Visibility);
            Assert.True(layout.Get("profile")["f2"].IsHidden);
        }
        finally { menu.IsOpen = false; }

        // Same-schema profiles reuse the actual columns: hidden state must not
        // leak to another profile which has no saved layout of its own.
        DynamicProfileGrid.SetProfileId(grid, "other-profile");
        Flush();
        Assert.Equal(Visibility.Visible, grid.Columns[4].Visibility);
        DynamicProfileGrid.SetProfileId(grid, "profile");
        Flush();
        Assert.Equal(Visibility.Collapsed, grid.Columns[4].Visibility);
        foreach (var width in new[] { 660d, 1140d })
        {
            grid.Width = width;
            grid.Measure(new Size(width, 400)); grid.Arrange(new Rect(0, 0, width, 400)); Flush();
            Assert.Equal(Visibility.Collapsed, grid.Columns[4].Visibility);
            Assert.True(layout.Get("profile")["f2"].IsHidden);
        }

        DynamicProfileGrid.ShowColumnsMenu(grid, grid);
        menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
        try
        {
            var restore = Assert.Single(menu.Items.OfType<MenuItem>(), item => !item.IsCheckable);
            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, restore));
            Flush();
            Assert.All(grid.Columns.Skip(2), column => Assert.Equal(Visibility.Visible, column.Visibility));
            Assert.All(layout.Get("profile").Values, state =>
            {
                Assert.False(state.IsHidden);
                Assert.Equal(GridColumnWidthMode.Auto, state.WidthMode);
            });
        }
        finally { menu.IsOpen = false; }

        DynamicProfileGrid.ShowColumnsMenu(grid, grid);
        menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
        try
        {
            grid.IsEnabled = false;
            Flush();
            Assert.False(menu.IsOpen);
            DynamicProfileGrid.ShowColumnsMenu(grid, grid);
            Assert.True(grid.ContextMenu is null || !grid.ContextMenu.IsOpen);
        }
        finally { grid.IsEnabled = true; menu.IsOpen = false; }
    }

    private static void VerifyOnlyUtilityColumnsStayFrozen(DataGrid grid, MemoryLayoutStore layout, MemoryPresentationStore presentation)
    {
        presentation.Change("f0", ColumnWidthPreset.Wide);
        Flush();
        layout.Save("profile", layout.Get("profile").Values.Select(state => state.FieldId == "f0"
            ? state with { Width = 530, WidthMode = GridColumnWidthMode.Manual } : state));
        typeof(DynamicProfileGrid).GetMethod("ApplyLayout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [grid]);
        Flush();
        foreach (var width in new[] { 1140d, 660d, 1140d })
        {
            grid.Width = width;
            grid.Measure(new Size(width, 400)); grid.Arrange(new Rect(0, 0, width, 400)); Flush();
            Assert.Equal(530, grid.Columns[2].Width.Value);
            Assert.Equal(2, grid.FrozenColumnCount);
            Assert.Equal(64, grid.Columns[0].Width.Value);
            Assert.Equal(48, grid.Columns[1].Width.Value);
            var pinnedWidth = grid.Columns.OrderBy(column => column.DisplayIndex)
                .Take(grid.FrozenColumnCount).Sum(column => column.Width.Value);
            Assert.True(grid.ActualWidth - pinnedWidth >= 160,
                "Only utility columns may occupy the frozen horizontal viewport.");
            VerifyFirstProfileFieldScrolls(grid);
        }
    }

    private static void VerifyFirstProfileFieldScrolls(DataGrid grid)
    {
        grid.UpdateLayout();
        var scroll = Assert.Single(FindVisualChildren<ScrollViewer>(grid), viewer => viewer.Name == "DG_ScrollViewer");
        scroll.ScrollToHorizontalOffset(0);
        Flush();
        grid.UpdateLayout();
        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
        var cells = FindVisualChildren<DataGridCell>(row).ToArray();
        var selection = Assert.Single(cells, cell => cell.Column == grid.Columns[0]);
        var browser = Assert.Single(cells, cell => cell.Column == grid.Columns[1]);
        var firstField = Assert.Single(cells, cell => cell.Column == grid.Columns[2]);
        var selectionStart = selection.TranslatePoint(new Point(), grid).X;
        var browserStart = browser.TranslatePoint(new Point(), grid).X;
        var fieldStart = firstField.TranslatePoint(new Point(), grid).X;

        Assert.True(scroll.ScrollableWidth > 0);
        scroll.ScrollToHorizontalOffset(Math.Min(160, scroll.ScrollableWidth));
        Flush();
        grid.UpdateLayout();
        Assert.True(scroll.HorizontalOffset > 0);
        Assert.InRange(Math.Abs(selection.TranslatePoint(new Point(), grid).X - selectionStart), 0, 0.1);
        Assert.InRange(Math.Abs(browser.TranslatePoint(new Point(), grid).X - browserStart), 0, 0.1);
        Assert.True(firstField.TranslatePoint(new Point(), grid).X < fieldStart - 1,
            "Horizontal scrolling must move the first profile field, not start at the second data column.");
        scroll.ScrollToHorizontalOffset(0);
        Flush();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class MemoryLayoutStore : IGridLayoutStore
    {
        private readonly Dictionary<string, Dictionary<string, GridColumnLayout>> _profiles = [];
        public IReadOnlyDictionary<string, GridColumnLayout> Get(string id) =>
            _profiles.GetValueOrDefault(id) ?? new Dictionary<string, GridColumnLayout>();
        public void Save(string id, IEnumerable<GridColumnLayout> columns) =>
            _profiles[id] = columns.ToDictionary(static c => c.FieldId);
    }
    private sealed class MemoryPresentationStore : IProfilePresentationStore
    {
        private readonly Dictionary<string, ColumnWidthPreset> _presets = new() { ["f0"] = ColumnWidthPreset.Narrow, ["f2"] = ColumnWidthPreset.Wide };
        public event EventHandler<ProfilePresentationChangedEventArgs>? Changed;
        public Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, ColumnWidthPreset>>(_presets);
        public Task SaveAsync(string id, IReadOnlyDictionary<string, ColumnWidthPreset> fields, CancellationToken ct = default) => Task.CompletedTask;
        public void Change(string id, ColumnWidthPreset value)
        {
            _presets[id] = value;
            Changed?.Invoke(this, new("profile", [id]));
        }
    }
}
