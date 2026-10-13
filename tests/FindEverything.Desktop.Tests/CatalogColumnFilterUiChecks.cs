using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            Field("date", ProfileFieldValueKind.DateTime), Field("flag", ProfileFieldValueKind.Boolean),
            Field("document", ProfileFieldValueKind.String) with { SourceKind = ProfileFieldSourceKind.TextFileContent }
        };
        owner.Fields = fields;
        var examples = (List<CatalogItemViewModel>)typeof(CatalogViewModel).GetField("_loadedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        for (var index = 0; index < 2; index++)
        {
            var values = new Dictionary<string, object?> { ["name"] = "Apollo", ["amount"] = (decimal)(index * 10),
                ["date"] = new DateTime(2026, 5, 21).AddDays(index), ["flag"] = index == 0,
                ["document"] = "Example text-file content" };
            examples.Add(new(new CatalogItem(@"C:\Archive\Apollo\item-" + index, "item-" + index, "rule", values, values, false), fields, owner.Localizer));
        }
        owner.Items.Refresh();
        var grid = new Ui.DataGrid
        {
            DataContext = owner, AutoGenerateColumns = false, CanUserAddRows = false,
            ItemsSource = owner.Items, Style = System.Windows.Application.Current!.TryFindResource("FluentDataGridStyle") as Style
        };
        DynamicProfileGrid.SetLocalizer(grid, owner.Localizer);
        DynamicProfileGrid.SetFields(grid, fields);
        var workspace = new Grid(); workspace.Children.Add(grid);
        var window = new Window { Content = workspace, Width = 1000, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show(); grid.UpdateLayout();
            PumpUntil(() => grid.Columns[2].Header is StackPanel && grid.Columns[6].Header is StackPanel);
            workspace.UpdateLayout();
            var workspaceSize = workspace.RenderSize;
            var gridSize = grid.RenderSize;
            var documentHeader = Assert.IsType<StackPanel>(DynamicProfileGrid.GetColumnHeaderContent(grid.Columns[6]));
            var documentRefresh = Assert.Single(documentHeader.Children.OfType<Ui.Button>());
            Assert.Same(owner.RefreshTextColumnCommand, documentRefresh.Command);
            Assert.Equal("document", documentRefresh.CommandParameter);
            Assert.Same(documentHeader, Assert.IsType<StackPanel>(grid.Columns[6].Header).Children[0]);
            var stateProperty = (DependencyProperty)typeof(DynamicProfileGrid).GetField("ColumnFiltersStateProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var state = grid.GetValue(stateProperty);
            Assert.NotNull(state);
            var flyout = (Ui.Flyout)state.GetType().GetField("Flyout")!.GetValue(state)!;
            var nativeSorts = 0;
            grid.Sorting += (_, _) => nativeSorts++;
            DataGridColumnHeader Header(int columnIndex)
            {
                grid.UpdateLayout();
                return Descendants<DataGridColumnHeader>(grid).First(candidate => candidate.Column == grid.Columns[columnIndex]);
            }
            void Show(int columnIndex, Key? key = null)
            {
                flyout.Hide();
                var header = Header(columnIndex);
                // Exercise real routed input. Calling the private opener directly
                // would miss guards which mistake the header for a nested button.
                if (key is { } activation) Assert.True(RaiseHeaderKey(header, activation).Handled);
                else
                {
                    RaiseHeaderMouse(header, UIElement.PreviewMouseLeftButtonDownEvent);
                    Assert.True(RaiseHeaderMouse(header, UIElement.PreviewMouseLeftButtonUpEvent).Handled);
                }
                var popup = Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout));
                PumpUntil(() => flyout.IsLoaded && popup.IsLoaded && flyout.IsOpen && popup.IsOpen);
                Assert.Same(header, popup.PlacementTarget);
                Assert.Same(workspace, VisualTreeHelper.GetParent(flyout));
                workspace.UpdateLayout();
                Assert.Equal(workspaceSize, workspace.RenderSize);
                Assert.Equal(gridSize, grid.RenderSize);
                Assert.Equal(0, flyout.ActualWidth); Assert.Equal(0, flyout.ActualHeight);
                Assert.True(flyout.IsHitTestVisible);
                Assert.False(grid.Columns[columnIndex].CanUserSort);
                Assert.Equal(0, nativeSorts);
            }
            Show(2);
            var body = Assert.IsType<StackPanel>(flyout.Content);
            var mode = body.Children.OfType<ComboBox>().Single();
            mode.SelectedIndex = 1;
            var text = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.TextBox>()).Single();
            text.Text = "Apollo";
            Assert.Equal("Apollo", Assert.Single(owner.ActiveColumnFilters).Filter.Text);
            CaptureFlyoutQa(grid, flyout, "catalog-filter-text.png");
            Show(3, Key.Enter);
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            Assert.Equal(2, body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Slider>()).Count());
            var numbers = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.TextBox>()).ToArray();
            numbers[0].Text = "1"; numbers[1].Text = "2";
            Assert.Equal(2, owner.ActiveColumnFilters.Count);
            CaptureFlyoutQa(grid, flyout, "catalog-filter-number.png");
            numbers[0].Text = "3";
            Assert.Equal(1m, owner.GetColumnFilter(CatalogColumnTarget.Field("amount"))!.Minimum);
            Show(4, Key.F4);
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            var dates = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Ui.CalendarDatePicker>()).ToArray();
            Assert.Equal(2, dates.Length);
            dates[0].SetCurrentValue(Ui.CalendarDatePicker.DateProperty, new DateTime(2026, 5, 21));
            dates[1].SetCurrentValue(Ui.CalendarDatePicker.DateProperty, new DateTime(2026, 5, 22));
            Assert.Equal(new DateTime(2026, 5, 22), owner.GetColumnFilter(CatalogColumnTarget.Field("date"))!.EndDate);
            CaptureFlyoutQa(grid, flyout, "catalog-filter-date.png");
            Show(5, Key.Space);
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 1;
            Assert.True(owner.GetColumnFilter(CatalogColumnTarget.Field("flag"))!.Boolean);
            CaptureFlyoutQa(grid, flyout, "catalog-filter-boolean.png");
            owner.ApplyColumnSort(CatalogColumnTarget.Field("amount"), true);
            Assert.True(owner.IsColumnSorted(CatalogColumnTarget.Field("amount")));
            owner.ClearColumnFiltersCommand.Execute(null);
            Assert.False(owner.HasColumnFilters);
            Assert.Empty(owner.ActiveColumnFilters);
            flyout.Hide();
            RaiseHeaderMouse(documentRefresh, UIElement.PreviewMouseLeftButtonDownEvent);
            Assert.False(RaiseHeaderMouse(documentRefresh, UIElement.PreviewMouseLeftButtonUpEvent).Handled);
            Assert.False(flyout.IsOpen);
            Assert.False(RaiseHeaderKey(documentRefresh, Key.F4).Handled);
            Assert.False(flyout.IsOpen);

            var dragHeader = Header(2);
            var down = RaiseHeaderMouse(dragHeader, UIElement.PreviewMouseLeftButtonDownEvent);
            // Simulate displacement without changing the machine's mouse state.
            // The actual routed Up must reject a drag even if Move was missed.
            var position = down.GetPosition(grid);
            state.GetType().GetField("Start")!.SetValue(state, new Point(
                position.X - SystemParameters.MinimumHorizontalDragDistance - 1, position.Y));
            Assert.False(RaiseHeaderMouse(dragHeader, UIElement.PreviewMouseLeftButtonUpEvent).Handled);
            Assert.False(flyout.IsOpen);
            var resizeThumb = Descendants<Thumb>(dragHeader).First();
            RaiseHeaderMouse(resizeThumb, UIElement.PreviewMouseLeftButtonDownEvent);
            Assert.False(RaiseHeaderMouse(resizeThumb, UIElement.PreviewMouseLeftButtonUpEvent).Handled);
            Assert.False(flyout.IsOpen);
            Assert.Equal(0, nativeSorts);
            var hostCount = workspace.Children.Count;
            grid.DataContext = null;
            PumpUntil(() => !workspace.Children.Contains(flyout));
            Assert.DoesNotContain(flyout, workspace.Children.Cast<UIElement>());
            grid.DataContext = owner;
            PumpUntil(() => flyout.IsLoaded);
            Assert.Equal(hostCount, workspace.Children.Count);
            Show(2);
            flyout.Hide();
            VerifyLoadedSchemaReplacementWhilePaging(grid, owner, fields);
        }
        finally { window.Close(); }
        PumpUntil(() => workspace.Children.Count == 1);
        Assert.Same(grid, workspace.Children[0]);
        VerifyLiveFullText(services);
        VerifySupersededNumericRange(services);
    }

    private static MouseButtonEventArgs RaiseHeaderMouse(UIElement source, RoutedEvent routedEvent)
    {
        // WPF's button-specific preview events are Direct. Real mouse input
        // tunnels the generic event, which re-raises those events on each ancestor.
        // Raising LeftButtonUp only on the header would never reach the grid.
        var inputEvent = routedEvent == UIElement.PreviewMouseLeftButtonDownEvent
            ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent;
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = inputEvent };
        source.RaiseEvent(args);
        return args;
    }

    private static KeyEventArgs RaiseHeaderKey(UIElement source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, key)
            { RoutedEvent = UIElement.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        return args;
    }

    private static void CaptureFlyoutQa(Ui.DataGrid grid, Ui.Flyout flyout, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var popup = Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout));
        grid.UpdateLayout();
        // The template's IsOpen binding/native popup arrange can still be queued
        // after Flyout.Show updates the owner's flag. Wait for the actual displayed
        // surface, and fail boundedly if filtering unexpectedly closes it.
        PumpUntil(() => flyout.IsLoaded && popup.IsLoaded && flyout.IsOpen && popup.IsOpen && popup.Child is FrameworkElement child
            && child.IsMeasureValid && child.IsArrangeValid && child.ActualWidth > 0 && child.ActualHeight > 0);
        Assert.True(popup.IsOpen);
        var root = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        root.UpdateLayout(); grid.UpdateLayout();
        Assert.True(flyout.IsOpen && popup.IsOpen && root.IsMeasureValid && root.IsArrangeValid
            && root.ActualWidth > 0 && root.ActualHeight > 0);
        var width = checked((int)Math.Ceiling(root.ActualWidth));
        var height = checked((int)Math.Ceiling(root.ActualHeight));
        var background = root.TryFindResource("ApplicationBackgroundBrush") as Brush
            ?? root.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
        System.IO.Directory.CreateDirectory(directory);
        var popupBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
        popupBitmap.Render(backdrop); popupBitmap.Render(root);
        Save(popupBitmap, fileName);

        // Popup HWNDs are outside the window's visual tree. Preserve the actual
        // popup render above, and add a separate context composite using the real
        // grid header strip and native popup contents, not a recreated mockup.
        var presenter = Descendants<DataGridColumnHeadersPresenter>(grid).Single();
        var headerHeight = presenter.ActualHeight;
        Assert.True(headerHeight > 0);
        const int margin = 16;
        var contextWidth = checked((int)Math.Ceiling(Math.Max(grid.ActualWidth, width) + margin * 2));
        var contextHeight = checked((int)Math.Ceiling(headerHeight + height + margin * 3));
        var context = new DrawingVisual();
        using (var drawing = context.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, contextWidth, contextHeight));
            var headers = new VisualBrush(grid) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, grid.ActualWidth, headerHeight), Stretch = Stretch.Fill };
            drawing.DrawRectangle(headers, null, new Rect(margin, margin, grid.ActualWidth, headerHeight));
            var anchor = Assert.IsType<DataGridColumnHeader>(popup.PlacementTarget);
            var x = anchor.TransformToAncestor(grid).Transform(new Point()).X;
            drawing.DrawImage(popupBitmap, new Rect(Math.Clamp(margin + x, margin, contextWidth - width - margin),
                margin * 2 + headerHeight, width, height));
        }
        var contextBitmap = new RenderTargetBitmap(contextWidth, contextHeight, 96, 96, PixelFormats.Pbgra32);
        contextBitmap.Render(context);
        Save(contextBitmap, System.IO.Path.GetFileNameWithoutExtension(fileName) + "-context.png");

        void Save(BitmapSource bitmap, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, name)); encoder.Save(stream);
        }
    }

    private static void VerifySupersededNumericRange(IServiceProvider services)
    {
        var store = new DelayedNumericRangeStore();
        using var owner = ActivatorUtilities.CreateInstance<CatalogViewModel>(services, store);
        var fields = new[] { Field("amount", ProfileFieldValueKind.Decimal) };
        owner.Fields = fields;
        var scope = new CatalogProjectionScope("range-ui", "range-ui", @"C:\Archive", @"C:\metadata.db", "range-ui", owner.Localizer.Culture.Name, null);
        var snapshot = new CatalogProjectionSnapshot("range-ui", scope, new(0, 0, 0, 0, 0, false, null, DateTimeOffset.UtcNow), fields);
        typeof(CatalogViewModel).GetField("_projectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, snapshot);
        owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("amount"), CatalogFilterOperator.NumberRange) { Minimum = 1 });
        var grid = new Ui.DataGrid { DataContext = owner, AutoGenerateColumns = false, CanUserAddRows = false,
            ItemsSource = owner.Items, Style = System.Windows.Application.Current!.TryFindResource("FluentDataGridStyle") as Style };
        DynamicProfileGrid.SetLocalizer(grid, owner.Localizer); DynamicProfileGrid.SetFields(grid, fields);
        var window = new Window { Content = grid, Width = 700, Height = 240, ShowInTaskbar = false };
        try
        {
            window.Show(); grid.UpdateLayout(); PumpUntil(() => grid.Columns[2].Header is StackPanel);
            var stateProperty = (DependencyProperty)typeof(DynamicProfileGrid).GetField("ColumnFiltersStateProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var state = grid.GetValue(stateProperty);
            var flyout = (Ui.Flyout)state.GetType().GetField("Flyout")!.GetValue(state)!;
            var open = typeof(DynamicProfileGrid).GetMethod("OpenColumnFlyoutAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task Open()
            {
                var header = Descendants<DataGridColumnHeader>(grid).First(candidate => candidate.Column == grid.Columns[2]);
                return (Task)open.Invoke(null, [grid, state, owner, header, CatalogColumnTarget.Field("amount")])!;
            }
            var first = Open();
            Assert.Single(store.Requests);
            var nativePopup = Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout));
            PumpUntil(() => flyout.IsLoaded && nativePopup.IsLoaded && flyout.IsOpen && nativePopup.IsOpen);
            Assert.Same(grid, window.Content);
            var body = Assert.IsType<StackPanel>(flyout.Content); var mode = body.Children.OfType<ComboBox>().Single();
            var oldSliders = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Slider>()).ToArray();
            mode.SelectedIndex = 0; mode.SelectedIndex = 1;
            Assert.Equal(2, store.Requests.Count);
            var currentSliders = body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Slider>()).ToArray();
            Assert.NotSame(oldSliders[0], currentSliders[0]);
            store.Requests[0].SetResult(new(1, 2)); PumpUntil(() => first.IsCompleted); first.GetAwaiter().GetResult();
            Assert.All(currentSliders, slider => Assert.False(slider.IsEnabled));
            store.Requests[1].SetResult(new(10, 20)); PumpUntil(() => currentSliders.All(slider => slider.IsEnabled));
            currentSliders[0].Value = 5000;
            Assert.Equal(15m, owner.GetColumnFilter(CatalogColumnTarget.Field("amount"))!.Minimum);

            var failed = Open(); Assert.Equal(3, store.Requests.Count);
            body = Assert.IsType<StackPanel>(flyout.Content); mode = body.Children.OfType<ComboBox>().Single(); mode.SelectedIndex = 0;
            store.Requests[2].SetException(new System.IO.IOException("A superseded range read failed."));
            PumpUntil(() => failed.IsCompleted); failed.GetAwaiter().GetResult();
            var staleError = body.Children.OfType<Ui.TextBlock>().Single(block => block.Text == string.Empty);
            Assert.Equal(Visibility.Collapsed, staleError.Visibility);
        }
        finally { window.Close(); }
    }

    private sealed class DelayedNumericRangeStore : ICatalogProjectionStore
    {
        public List<TaskCompletionSource<CatalogNumericRange?>> Requests { get; } = [];
        public Task<CatalogNumericRange?> ReadNumericRangeAsync(CatalogProjectionSnapshot snapshot, string fieldId, CancellationToken token = default)
        {
            var completion = new TaskCompletionSource<CatalogNumericRange?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(completion); return completion.Task;
        }
        public Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope, CancellationToken token = default) => Task.FromResult<CatalogProjectionSnapshot?>(null);
        public ICatalogProjectionWriter Begin(CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields) => throw new NotSupportedException();
        public IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot, CatalogProjectionQuery query) => new EmptyRangeQuery(snapshot.Generation);
        public IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer, CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields, CatalogProjectionQuery query) => throw new NotSupportedException();
        public Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot, string fieldId,
            Func<string, CancellationToken, Task<TextColumnReadResult>> readValue, IProgress<TextColumnRefreshProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class EmptyRangeQuery(string identity) : IResultQuerySession<CatalogProjectionRow>
    {
        public string Identity => identity;
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<CatalogProjectionRow>([], offset, false));
        public async IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
                && grid.Columns.Skip(2).All(column => column.Header is StackPanel));
            Assert.IsType<Ui.SymbolIcon>(grid.Columns[1].Header);
            Assert.Equal(replacementFields[0].Header, DynamicProfileGrid.GetColumnHeaderContent(grid.Columns[2]));
            Assert.All(grid.Columns.Skip(2), column => Assert.False(column.CanUserSort));
        }
        finally
        {
            owner.Fields = initialFields;
            DynamicProfileGrid.SetFields(grid, initialFields);
            grid.ItemsSource = owner.Items;
        }
    }

    private static void VerifyLiveFullText(IServiceProvider services)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "findeverything-live-filter-ui-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteCatalogProjectionStore(System.IO.Path.Combine(directory, "catalog.db"));
            using var owner = ActivatorUtilities.CreateInstance<CatalogViewModel>(services, store);
            Assert.Same(store, typeof(CatalogViewModel).GetField("_projectionStore",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
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
                .Invoke(owner, [false, true, CancellationToken.None, false])!;
            PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
            Assert.Equal(0, owner.Items.Count);
            Assert.True(owner.HasLoadedItems, owner.StatusMessage); // Empty filters must not hide the headers/search needed to clear them.
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
