using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
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
        public INotifyCollectionChanged? Source;
        public EventHandler<NotifyCollectionChangedEventArgs>? SourceHandler;
        public DependencyPropertyDescriptor? SourceDescriptor;
        public EventHandler? SourceChangedHandler;
        public bool IsObservingSource;
        public IProfilePresentationStore? Store;
        public EventHandler<ProfilePresentationChangedEventArgs>? StoreHandler;
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
            AttachPresentation(grid); ObserveSource(grid); ScheduleSizing(grid);
        };
        grid.Unloaded += (_, _) =>
        {
            var state = GetSizing(grid);
            if (state.Store is not null) state.Store.Changed -= state.StoreHandler;
            state.Store = null;
            if (state.Source is not null && state.SourceHandler is not null) CollectionChangedEventManager.RemoveHandler(state.Source, state.SourceHandler);
            state.Source = null;
            if (state.IsObservingSource)
            {
                state.SourceDescriptor!.RemoveValueChanged(grid, state.SourceChangedHandler!);
                state.IsObservingSource = false;
            }
            state.Version++;
        };
        grid.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) ScheduleSizing(grid); };
        grid.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnHeaderContextMenuOpening), true);
        var initial = GetSizing(grid);
        initial.SourceDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))!;
        initial.SourceChangedHandler = (_, _) => ObserveSource(grid);
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
        state.MeasuredWidths.Clear();
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
        if (grid.Columns.Count <= 1 || grid.ActualWidth <= 0) return;
        var state = GetSizing(grid);
        if (state.ProfileId != GetProfileId(grid)) { _ = LoadPresetsAsync(grid); return; }
        var fields = (GetFields(grid) as IEnumerable)?.OfType<ProfileFieldDescriptor>().ToDictionary(static f => f.FieldId, StringComparer.OrdinalIgnoreCase) ?? [];
        var sample = !state.HasMeasured ? GetSample(grid.ItemsSource) : [];
        if (sample.Length > 0)
        {
            var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
            var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
            var culture = GetLocalizer(grid)?.Culture ?? CultureInfo.CurrentCulture;
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);
            double Measure(string text)
            {
                text = text.Length > 512 ? text[..512] : text;
                if (!cache.TryGetValue(text, out var width)) cache[text] = width = new FormattedText(text, culture,
                    FlowDirection.LeftToRight, typeface, grid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
                return width;
            }
            foreach (var column in grid.Columns.Skip(1))
            {
                if (column.GetValue(FieldIdProperty) is not string id) continue;
                var header = fields.TryGetValue(id, out var field) ? field.Header : GetColumnHeaderContent(column)?.ToString() ?? "";
                // Filter/sort markers need their own header gutter, independent
                // of text-cell padding and the optional text refresh action.
                var desired = Measure(header) + (field?.SourceKind == ProfileFieldSourceKind.TextFileContent ? 96 : 60);
                foreach (var item in sample)
                {
                    var text = id == PathColumnId ? item.FullPath : id == StatusColumnId ? item.CoverageText
                        : item.DisplayValues.GetValueOrDefault(id) ?? "";
                    desired = Math.Max(desired, Measure(text) + 28
                        + (item.MappedValueIndicators.GetValueOrDefault(id) ? 30 : 0)
                        + (item.FieldErrors.ContainsKey(id) ? 22 : 0)
                        + (item.PreviewTooltips.ContainsKey(id) ? 22 : 0));
                }
                state.MeasuredWidths[id] = desired;
            }
            state.HasMeasured = true;
        }
        var auto = grid.Columns.Skip(1).Where(c => !(bool)c.GetValue(IsManualWidthProperty)).ToArray();
        foreach (var column in grid.Columns.Skip(1))
        {
            var (min, max) = Bounds((string)column.GetValue(FieldIdProperty), fields, state);
            column.MinWidth = min;
            column.MaxWidth = max;
        }
        var available = Math.Max(0, grid.ActualWidth - 64 - SystemParameters.VerticalScrollBarWidth
            - grid.Columns.Skip(1).Where(c => (bool)c.GetValue(IsManualWidthProperty)).Sum(static c => c.ActualWidth));
        var weights = auto.Sum(c => Weight((string)c.GetValue(FieldIdProperty), state));
        foreach (var column in auto)
        {
            var id = (string)column.GetValue(FieldIdProperty);
            var (min, max) = Bounds(id, fields, state);
            column.MinWidth = min;
            column.MaxWidth = max;
            var baseline = weights > 0 ? available * Weight(id, state) / weights : min;
            column.Width = new DataGridLength(Math.Clamp(Math.Max(baseline, state.MeasuredWidths.GetValueOrDefault(id)), min, max));
        }
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
            SizeColumns(grid);
            SaveLayout(grid);
        };
        menu.Items.Add(reset);
        menu.PlacementTarget = grid;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
