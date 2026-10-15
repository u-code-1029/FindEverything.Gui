using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Behaviors;

public static partial class DynamicProfileGrid
{
    private const string StatusColumnId = "$ui:status";
    private const string PathColumnId = "$ui:path";
    private static readonly DependencyProperty IsManualWidthProperty = DependencyProperty.RegisterAttached(
        "IsManualWidth", typeof(bool), typeof(DynamicProfileGrid), new PropertyMetadata(false));
    private static readonly DependencyProperty SizingProperty = DependencyProperty.RegisterAttached(
        "Sizing", typeof(SizingState), typeof(DynamicProfileGrid));
    public static readonly DependencyProperty PresentationStoreProperty = DependencyProperty.RegisterAttached(
        "PresentationStore", typeof(IProfilePresentationStore), typeof(DynamicProfileGrid),
        new PropertyMetadata(null, OnPresentationStoreChanged));

    public static void SetPresentationStore(DependencyObject element, IProfilePresentationStore? value) => element.SetValue(PresentationStoreProperty, value);
    public static IProfilePresentationStore? GetPresentationStore(DependencyObject element) => (IProfilePresentationStore?)element.GetValue(PresentationStoreProperty);

    private sealed class SizingState
    {
        public string? Schema;
        public string? ProfileId;
        public int Version;
        public bool Pending;
        public bool HasMeasured;
        public IReadOnlyDictionary<string, ColumnWidthPreset> Presets = new Dictionary<string, ColumnWidthPreset>();
        public readonly Dictionary<string, double> MeasuredWidths = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> TextWidths = new(StringComparer.Ordinal);
        public string? TextMetrics;
        public MeasurementWork? Measurement;
        public INotifyCollectionChanged? Source;
        public EventHandler<NotifyCollectionChangedEventArgs>? SourceHandler;
        public DependencyPropertyDescriptor? SourceDescriptor;
        public EventHandler? SourceChangedHandler;
        public bool IsObservingSource;
        public IProfilePresentationStore? Store;
        public EventHandler<ProfilePresentationChangedEventArgs>? StoreHandler;
        public IGridLayoutStore? LayoutStore;
        public EventHandler<GridLayoutChangedEventArgs>? LayoutHandler;
        public long LayoutRevision;
    }

    private sealed class MeasurementWork(CatalogItemViewModel[] rows, DataGridColumn[] columns,
        IReadOnlyDictionary<string, ProfileFieldDescriptor> fields, Typeface typeface, double fontSize, double dpi, CultureInfo culture)
    {
        public readonly CatalogItemViewModel[] Rows = rows;
        public readonly DataGridColumn[] Columns = columns;
        public readonly IReadOnlyDictionary<string, ProfileFieldDescriptor> Fields = fields;
        public readonly Typeface Typeface = typeface;
        public readonly double FontSize = fontSize;
        public readonly double Dpi = dpi;
        public readonly CultureInfo Culture = culture;
        public int Column;
        public int Row;
        public double Desired;
    }

    private static SizingState GetSizing(DataGrid grid) => (SizingState)grid.GetValue(SizingProperty);

    private static string GetSchemaSignature(DataGrid grid, ProfileFieldDescriptor[] fields) =>
        GetLocalizer(grid)?.Culture.Name + "|" + string.Join("|", fields.Select(static field =>
            $"{field.FieldId}:{field.SourceKind}:{field.Header}:{field.Placeholder}:{field.Order}"));

    private static void InitializeSizing(DataGrid grid)
    {
        if (grid.GetValue(SizingProperty) is SizingState) return;
        grid.SetValue(SizingProperty, new SizingState());
        grid.Loaded += (_, _) =>
        {
            var state = GetSizing(grid);
            if (!state.IsObservingSource)
            {
                state.SourceDescriptor!.AddValueChanged(grid, state.SourceChangedHandler!);
                state.IsObservingSource = true;
            }
            AttachLayoutStore(grid); ApplyLayout(grid);
            AttachPresentation(grid); ObserveSource(grid); ScheduleSizing(grid);
        };
        grid.Unloaded += (_, _) =>
        {
            var state = GetSizing(grid);
            if (state.Store is not null) state.Store.Changed -= state.StoreHandler;
            state.Store = null;
            if (state.LayoutStore is not null) state.LayoutStore.Changed -= state.LayoutHandler;
            state.LayoutStore = null;
            if (state.Source is not null && state.SourceHandler is not null) CollectionChangedEventManager.RemoveHandler(state.Source, state.SourceHandler);
            state.Source = null;
            if (state.IsObservingSource)
            {
                state.SourceDescriptor!.RemoveValueChanged(grid, state.SourceChangedHandler!);
                state.IsObservingSource = false;
            }
            state.Version++;
            state.Measurement = null;
        };
        grid.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) ScheduleSizing(grid); };
        grid.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnHeaderContextMenuOpening), true);
        var initial = GetSizing(grid);
        initial.SourceDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))!;
        initial.SourceChangedHandler = (_, _) => ObserveSource(grid);
    }

    private static void AttachLayoutStore(DataGrid grid)
    {
        var state = GetSizing(grid);
        var store = grid.IsLoaded ? GetLayoutStore(grid) : null;
        if (ReferenceEquals(state.LayoutStore, store)) return;
        if (state.LayoutStore is not null) state.LayoutStore.Changed -= state.LayoutHandler;
        state.LayoutStore = store;
        state.LayoutHandler = (_, change) =>
        {
            // Revision-aware saves also protect the gap before this UI callback runs.
            _ = grid.Dispatcher.InvokeAsync(() =>
            {
                if (!grid.IsLoaded || !ReferenceEquals(GetLayoutStore(grid), store) ||
                    !string.Equals(change.ProfileId, GetProfileId(grid), StringComparison.OrdinalIgnoreCase)) return;
                state.MeasuredWidths.Clear(); state.HasMeasured = false; state.Measurement = null;
                ApplyLayout(grid);
                UpdateFrozenColumns(grid);
            }, DispatcherPriority.Send);
        };
        if (store is not null) store.Changed += state.LayoutHandler;
    }

    private static void OnPresentationStoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        InitializeSizing(grid);
        AttachPresentation(grid);
    }

    private static void AttachPresentation(DataGrid grid)
    {
        var state = GetSizing(grid);
        var store = GetPresentationStore(grid);
        if (state.Store is not null) state.Store.Changed -= state.StoreHandler;
        state.Store = grid.IsLoaded ? store : null;
        state.StoreHandler = (_, e) =>
        {
            if (!string.Equals(e.ProfileId, state.ProfileId, StringComparison.OrdinalIgnoreCase)) return;
            _ = grid.Dispatcher.InvokeAsync(() =>
            {
                foreach (var column in grid.Columns)
                    if (column.GetValue(FieldIdProperty) is string id && e.ChangedFields.Contains(id, StringComparer.OrdinalIgnoreCase))
                        column.SetValue(IsManualWidthProperty, false);
                _ = LoadPresetsAsync(grid);
            });
        };
        if (state.Store is not null) state.Store.Changed += state.StoreHandler;
        _ = LoadPresetsAsync(grid);
    }

    private static async Task LoadPresetsAsync(DataGrid grid)
    {
        var state = GetSizing(grid);
        var profileId = GetProfileId(grid);
        var version = ++state.Version;
        if (state.ProfileId != profileId)
        {
            state.ProfileId = profileId;
            state.Presets = new Dictionary<string, ColumnWidthPreset>();
            state.HasMeasured = false;
            state.MeasuredWidths.Clear();
            state.Measurement = null;
        }
        if (GetPresentationStore(grid) is not { } store || string.IsNullOrWhiteSpace(profileId)) { ScheduleSizing(grid); return; }
        var presets = await store.LoadAsync(profileId);
        if (state.Version != version || GetProfileId(grid) != profileId) return;
        state.Presets = presets;
        ApplyLayout(grid);
        ScheduleSizing(grid);
    }

    private static void ObserveSource(DataGrid grid)
    {
        var state = GetSizing(grid);
        if (state.Source is not null && state.SourceHandler is not null) CollectionChangedEventManager.RemoveHandler(state.Source, state.SourceHandler);
        state.Source = grid.ItemsSource as INotifyCollectionChanged;
        state.HasMeasured = false;
        // A new filter/query keeps the same schema. Retain measured widths so
        // typing does not make every column jump, and reuse measured text.
        state.Measurement = null;
        state.SourceHandler ??= (_, _) => { if (!state.HasMeasured) ScheduleSizing(grid); };
        if (state.Source is not null) CollectionChangedEventManager.AddHandler(state.Source, state.SourceHandler);
        ScheduleSizing(grid);
    }

    private static void ScheduleSizing(DataGrid grid)
    {
        var state = GetSizing(grid);
        if (state.Pending) return;
        state.Pending = true;
        _ = grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            state.Pending = false;
            SizeColumns(grid);
        }));
    }

    private static void SizeColumns(DataGrid grid)
    {
        if (!grid.IsLoaded || !grid.IsVisible || grid.Columns.Count <= 1 || grid.ActualWidth <= 0) return;
        var state = GetSizing(grid);
        if (state.ProfileId != GetProfileId(grid)) { _ = LoadPresetsAsync(grid); return; }
        var fields = (GetFields(grid) as IEnumerable)?.OfType<ProfileFieldDescriptor>().ToDictionary(static f => f.FieldId, StringComparer.OrdinalIgnoreCase) ?? [];
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        var culture = GetLocalizer(grid)?.Culture ?? CultureInfo.CurrentCulture;
        var metrics = $"{grid.FontFamily}|{grid.FontStyle}|{grid.FontWeight}|{grid.FontStretch}|{grid.FontSize}|{dpi}|{culture.Name}";
        if (state.TextMetrics != metrics)
        {
            state.TextMetrics = metrics;
            state.TextWidths.Clear(); state.MeasuredWidths.Clear();
            state.Measurement = null; state.HasMeasured = false;
        }
        if (!state.HasMeasured && state.Measurement is null && GetSample(grid.ItemsSource) is { Length: > 0 } sample)
            state.Measurement = new(sample, grid.Columns.Skip(SelectionColumnOffset).OrderBy(column => column.DisplayIndex).ToArray(), fields,
                new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch), grid.FontSize, dpi, culture);
        if (state.Measurement is { } measurement)
        {
            if (AdvanceMeasurement(state, measurement)) { state.HasMeasured = true; state.Measurement = null; }
            else
            {
                // Keep the existing/default widths while measuring. Committing
                // each partial sample would repeatedly invalidate every column
                // and recycle its cells between the input-friendly time slices.
                ScheduleSizing(grid);
                return;
            }
        }
        var auto = grid.Columns.Skip(SelectionColumnOffset).Where(c => c.Visibility == Visibility.Visible && !(bool)c.GetValue(IsManualWidthProperty)).ToArray();
        foreach (var column in grid.Columns.Skip(SelectionColumnOffset))
        {
            var (min, max) = Bounds((string)column.GetValue(FieldIdProperty), fields, state);
            if ((bool)column.GetValue(IsManualWidthProperty) && column.Width.IsAbsolute)
            {
                // Imported explicit widths are intent, not an auto-sizing suggestion.
                min = Math.Min(min, column.Width.Value);
                max = Math.Max(max, column.Width.Value);
            }
            column.MinWidth = min;
            column.MaxWidth = max;
        }
        var available = Math.Max(0, grid.ActualWidth - 112 - SystemParameters.VerticalScrollBarWidth
            - grid.Columns.Skip(SelectionColumnOffset).Where(c => c.Visibility == Visibility.Visible && (bool)c.GetValue(IsManualWidthProperty)).Sum(static c => c.ActualWidth));
        var weights = auto.Sum(c => Weight((string)c.GetValue(FieldIdProperty), state));
        foreach (var column in auto)
        {
            var id = (string)column.GetValue(FieldIdProperty);
            var (min, max) = Bounds(id, fields, state);
            column.MinWidth = min;
            column.MaxWidth = max;
            var baseline = weights > 0 ? available * Weight(id, state) / weights : min;
            var width = Math.Clamp(Math.Max(baseline, state.MeasuredWidths.GetValueOrDefault(id)), min, max);
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > 0.5)
                column.Width = new DataGridLength(width);
        }
        UpdateFrozenColumns(grid);
    }

    private static bool AdvanceMeasurement(SizingState state, MeasurementWork work)
    {
        var started = Stopwatch.GetTimestamp();
        double Measure(string text)
        {
            if (text.Length > 512) text = text[..512];
            if (state.TextWidths.TryGetValue(text, out var cached)) return cached;
            var width = new FormattedText(text, work.Culture, FlowDirection.LeftToRight, work.Typeface,
                work.FontSize, Brushes.Black, work.Dpi).WidthIncludingTrailingWhitespace;
            if (state.TextWidths.Count >= 4096) state.TextWidths.Clear();
            state.TextWidths[text] = width;
            return width;
        }
        while (work.Column < work.Columns.Length)
        {
            var column = work.Columns[work.Column];
            if (column.GetValue(FieldIdProperty) is not string id || (bool)column.GetValue(IsManualWidthProperty))
            { work.Column++; work.Row = 0; continue; }
            work.Fields.TryGetValue(id, out var field);
            if (work.Row == 0)
                work.Desired = Measure(field?.Header ?? GetColumnHeaderContent(column)?.ToString() ?? "")
                    + (field?.SourceKind == ProfileFieldSourceKind.TextFileContent ? 96 : 60);
            while (work.Row < work.Rows.Length)
            {
                var item = work.Rows[work.Row++];
                var text = id == PathColumnId ? item.FullPath : id == StatusColumnId ? item.CoverageText : item.DisplayValues.GetValueOrDefault(id) ?? "";
                work.Desired = Math.Max(work.Desired, Measure(text) + 28
                    + (item.MappedValueIndicators.GetValueOrDefault(id) ? 30 : 0)
                    + (item.FieldErrors.ContainsKey(id) ? 22 : 0)
                    + (item.PreviewTooltips.ContainsKey(id) ? 22 : 0)
                    + (field?.SourceKind == ProfileFieldSourceKind.TextFileContent ? 76 : 0));
                // A dispatcher priority does not preempt a long callback. Yield
                // between text measurements so input can run during wide schemas.
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(3)) return false;
            }
            state.MeasuredWidths[id] = Math.Max(state.MeasuredWidths.GetValueOrDefault(id), work.Desired);
            work.Column++; work.Row = 0;
        }
        return true;
    }

    private static CatalogItemViewModel[] GetSample(IEnumerable? source) => source switch
    {
        ICollectionView view => GetSample(view.SourceCollection),
        VirtualResultCollection<CatalogItemViewModel> virtualRows => virtualRows.GetLoadedSample(200).ToArray(),
        IList rows => Enumerable.Range(0, Math.Min(rows.Count, 200)).Select(i => rows[i]).OfType<CatalogItemViewModel>().Where(static r => !r.IsPlaceholder).ToArray(),
        _ => [],
    };

    private static double Weight(string id, SizingState state) => id switch
    {
        PathColumnId => 1.8, StatusColumnId => 0.7,
        _ => state.Presets.GetValueOrDefault(id) switch { ColumnWidthPreset.Narrow => 0.7, ColumnWidthPreset.Wide => 1.6, _ => 1.0 },
    };
    private static (double Min, double Max) Bounds(string id, IReadOnlyDictionary<string, ProfileFieldDescriptor> fields, SizingState state)
    {
        if (id == PathColumnId) return (180, 720);
        if (id == StatusColumnId) return (80, 160);
        var bounds = state.Presets.GetValueOrDefault(id) switch
        {
            ColumnWidthPreset.Narrow => (72.0, 180.0), ColumnWidthPreset.Wide => (160.0, 560.0), _ => (96.0, 360.0),
        };
        return fields.TryGetValue(id, out var field) && field.SourceKind == ProfileFieldSourceKind.UserInput
            ? (Math.Max(120, bounds.Item1), bounds.Item2) : bounds;
    }

    private static void OnHeaderContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source
            || FindAncestor<DataGridColumnHeader>(source)?.Column is not { CanUserResize: true } column) return;
        var menu = new ContextMenu();
        var reset = new Wpf.Ui.Controls.MenuItem { Header = L(GetLocalizer(grid), "Loc.Catalog.Column.Width.Reset", "내용에 맞춰 너비 초기화") };
        reset.Click += (_, _) =>
        {
            column.SetValue(IsManualWidthProperty, false);
            var state = GetSizing(grid);
            state.HasMeasured = false;
            state.Measurement = null;
            if (column.GetValue(FieldIdProperty) is string fieldId) state.MeasuredWidths.Remove(fieldId);
            SizeColumns(grid);
            SaveLayout(grid);
        };
        menu.Items.Add(reset);
        menu.PlacementTarget = grid;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
