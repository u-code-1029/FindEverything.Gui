using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs inside the smoke test's existing application/STA dispatcher.</summary>
internal static class CatalogColumnFilterUiChecks
{
    public static void Verify(IServiceProvider services)
    {
        using var owner = ActivatorUtilities.CreateInstance<CatalogViewModel>(services);
        var fields = new[]
        {
            Field("name", ProfileFieldValueKind.String), Field("amount", ProfileFieldValueKind.Decimal),
            Field("date", ProfileFieldValueKind.DateTime), Field("flag", ProfileFieldValueKind.Boolean)
        };
        owner.Fields = fields;
        var grid = new Ui.DataGrid
        {
            DataContext = owner, AutoGenerateColumns = false, CanUserAddRows = false,
            ItemsSource = owner.Items, Style = System.Windows.Application.Current!.TryFindResource("FluentDataGridStyle") as Style
        };
        DynamicProfileGrid.SetLocalizer(grid, owner.Localizer);
        DynamicProfileGrid.SetFields(grid, fields);
        var window = new Window { Content = grid, Width = 1000, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show(); grid.UpdateLayout();
            PumpUntil(() => grid.Columns[2].Header is StackPanel);
            var stateProperty = (DependencyProperty)typeof(DynamicProfileGrid).GetField("ColumnFiltersStateProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var state = grid.GetValue(stateProperty);
            Assert.NotNull(state);
            var flyout = (Ui.Flyout)state.GetType().GetField("Flyout")!.GetValue(state)!;
            var open = typeof(DynamicProfileGrid).GetMethod("OpenColumnFlyoutAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            void Show(int columnIndex, CatalogColumnTarget target)
            {
                grid.UpdateLayout();
                var header = Descendants<DataGridColumnHeader>(grid).First(candidate => candidate.Column == grid.Columns[columnIndex]);
                ((Task)open.Invoke(null, [grid, state, owner, header, target])!).GetAwaiter().GetResult();
                Assert.True(flyout.IsOpen);
                Assert.Same(header, Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout)).PlacementTarget);
                Assert.False(grid.Columns[columnIndex].CanUserSort);
            }
            Show(2, CatalogColumnTarget.Field("name"));
            var body = Assert.IsType<StackPanel>(flyout.Content);
            var mode = body.Children.OfType<ComboBox>().Single();
            mode.SelectedIndex = 1;
            var text = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.TextBox>()).Single();
            text.Text = "Apollo";
            Assert.Equal("Apollo", Assert.Single(owner.ActiveColumnFilters).Filter.Text);
            Show(3, CatalogColumnTarget.Field("amount"));
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            Assert.Equal(2, body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Slider>()).Count());
            var numbers = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.TextBox>()).ToArray();
            numbers[0].Text = "1"; numbers[1].Text = "2";
            Assert.Equal(2, owner.ActiveColumnFilters.Count);
            numbers[0].Text = "3";
            Assert.Equal(1m, owner.GetColumnFilter(CatalogColumnTarget.Field("amount"))!.Minimum);
            Show(4, CatalogColumnTarget.Field("date"));
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            var dates = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.CalendarDatePicker>()).ToArray();
            Assert.Equal(2, dates.Length);
            dates[0].SetCurrentValue(Ui.CalendarDatePicker.DateProperty, new DateTime(2026, 5, 21));
            dates[1].SetCurrentValue(Ui.CalendarDatePicker.DateProperty, new DateTime(2026, 5, 22));
            Assert.Equal(new DateTime(2026, 5, 22), owner.GetColumnFilter(CatalogColumnTarget.Field("date"))!.EndDate);
            Show(5, CatalogColumnTarget.Field("flag"));
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            Assert.True(owner.GetColumnFilter(CatalogColumnTarget.Field("flag"))!.Boolean);
            owner.ApplyColumnSort(CatalogColumnTarget.Field("amount"), true);
            Assert.True(owner.IsColumnSorted(CatalogColumnTarget.Field("amount")));
            owner.ClearColumnFiltersCommand.Execute(null);
            Assert.False(owner.HasColumnFilters);
            Assert.Empty(owner.ActiveColumnFilters);
            VerifyLoadedSchemaReplacementWhilePaging(grid, owner, fields);
        }
        finally { window.Close(); }
        VerifyLiveFullText(services);
    }

    private static void VerifyLoadedSchemaReplacementWhilePaging(Ui.DataGrid grid, CatalogViewModel owner,
        ProfileFieldDescriptor[] initialFields)
    {
        var replacementFields = Enumerable.Range(0, 12).Select(index =>
            new ProfileFieldDescriptor($"replacement{index}", $"replacement{index}", $"Replacement {index}", index,
                false, ProfileFieldValueKind.String, true, null, null)).ToArray();
        var allFields = initialFields.Concat(replacementFields).ToArray();
        var rows = Enumerable.Range(0, 1500).Select(index =>
        {
            var values = allFields.ToDictionary(field => field.FieldId, field => field.Kind switch
            {
                ProfileFieldValueKind.Decimal => (object?)(decimal)index,
                ProfileFieldValueKind.DateTime => DateTime.Today.AddDays(index % 20),
                ProfileFieldValueKind.Boolean => index % 2 == 0,
                _ => $"row-{index}:{field.FieldId}"
            });
            return new CatalogItemViewModel(new CatalogItem(@"C:\paging\" + index, index.ToString(), "rule", values, values, false),
                allFields, owner.Localizer);
        }).ToArray();
        var pageLoads = 0;
        using var virtualRows = new VirtualResultCollection<CatalogItemViewModel>(async (offset, limit, token) =>
        {
            await Task.Delay(1, token).ConfigureAwait(false);
            return new(rows.Skip(offset).Take(limit).ToArray(), offset, offset + limit < rows.Length);
        }, _ => new CatalogItemViewModel(new CatalogItem("", "", "", new object(), new Dictionary<string, object?>(), false),
            allFields, owner.Localizer) { IsPlaceholder = true },
            action => grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            exception => throw new InvalidOperationException("Schema-replacement paging failed.", exception));
        virtualRows.PageLoaded += (_, _) => pageLoads++;
        virtualRows.Seed(new(rows.Take(500).ToArray(), 0, true), rows.Length);
        grid.ItemsSource = new ListCollectionView(virtualRows);
        try
        {
            // Each replacement raises Clear/Add while the headers presenter is
            // already subscribed. Header decoration must wait for the final schema,
            // including when a worker page arrives during dispatcher draining.
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var current = attempt % 2 == 0 ? initialFields : replacementFields;
                owner.Fields = current;
                DynamicProfileGrid.SetFields(grid, current);
                grid.ScrollIntoView(virtualRows[1100 + attempt]);
                grid.UpdateLayout();
            }
            PumpUntil(() => pageLoads > 0 && grid.Columns.Count == replacementFields.Length + 3
                && grid.Columns.Skip(1).All(column => column.Header is StackPanel));
            Assert.Equal(replacementFields[0].Header, DynamicProfileGrid.GetColumnHeaderContent(grid.Columns[2]));
            Assert.All(grid.Columns.Skip(1), column => Assert.False(column.CanUserSort));
        }
        finally { grid.ItemsSource = owner.Items; }
    }

    private static void VerifyLiveFullText(IServiceProvider services)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "findeverything-live-filter-ui-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteCatalogProjectionStore(System.IO.Path.Combine(directory, "catalog.db"));
            using var owner = ActivatorUtilities.CreateInstance<CatalogViewModel>(services, store);
            var fields = new[] { Field("text", ProfileFieldValueKind.String) with { SourceKind = ProfileFieldSourceKind.TextFileContent } };
            owner.Fields = fields;
            var scope = new CatalogProjectionScope("live-ui", "live-ui", directory, System.IO.Path.Combine(directory, "metadata.db"), "live-ui", owner.Localizer.Culture.Name, null);
            using var writer = store.Begin(scope, fields);
            var full = System.IO.Path.Combine(directory, "live");
            var values = new Dictionary<string, object?> { ["text"] = new string('a', 6000) + "TAIL_ONLY" };
            writer.Append(new CatalogItem(full, "live", "rule", values, values, false));
            var preview = new Dictionary<string, object?> { ["text"] = new string('a', 2048) };
            var loaded = (List<CatalogItemViewModel>)typeof(CatalogViewModel).GetField("_loadedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            loaded.Add(new(new CatalogItem(full, "live", "rule", preview, preview, false), fields, owner.Localizer) { PreviewFields = ["text"] });
            owner.Items.Refresh();
            owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.Contains) { Text = "TAIL_ONLY" });
            Assert.Empty(owner.Items.Cast<CatalogItemViewModel>());
            var version = (long)typeof(CatalogViewModel).GetField("_resultSessionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            var capture = typeof(CatalogViewModel).GetMethod("CaptureLiveFilterSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task.Run(() => capture.Invoke(owner, [writer, scope, fields, version])).GetAwaiter().GetResult();
            typeof(CatalogViewModel).GetMethod("ScheduleLiveFilterRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [false]);
            PumpUntil(() => owner.Items.Count == 1);
            Assert.Equal(2048, ((string)Assert.Single(owner.Items.Cast<CatalogItemViewModel>()).Values["text"]!).Length);
            // A changed stage revision must retain the last complete eligibility
            // while the replacement path-only query is still pending.
            writer.Reset();
            writer.Append(new CatalogItem(full, "live", "rule", values, values, false)); writer.Flush();
            typeof(CatalogViewModel).GetField("_liveLastFlushTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, 0L);
            Task.Run(() => capture.Invoke(owner, [writer, scope, fields, version])).GetAwaiter().GetResult();
            owner.Items.Refresh();
            Assert.Equal(1, owner.Items.Count);
            owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("text"), CatalogFilterOperator.DoesNotContain) { Text = "TAIL_ONLY" });
            PumpUntil(() => owner.Items.Count == 0);
            var snapshot = writer.Publish(new(1, 1, 0, 0, 0, false, null, DateTimeOffset.UtcNow));
            typeof(CatalogViewModel).GetField("_projectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, snapshot);
            var refresh = (Task)typeof(CatalogViewModel).GetMethod("RefreshProjectionQueryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [false, true, CancellationToken.None])!;
            PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
            Assert.Equal(0, owner.Items.Count);
            Assert.True(owner.HasLoadedItems); // Empty filters must not hide the headers/search needed to clear them.
        }
        finally
        {
            // Query lease cleanup is asynchronous and must finish before a Windows directory delete.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { System.IO.Directory.Delete(directory, recursive: true); break; }
                catch (System.IO.IOException) when (attempt < 9) { Thread.Sleep(20); }
            }
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (condition() || started.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "The asynchronous live filter did not update its bounded preview.");
    }

    private static ProfileFieldDescriptor Field(string id, ProfileFieldValueKind kind) => new(id, id, id, 0, false, kind, true, null, null);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
