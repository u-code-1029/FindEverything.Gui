using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Behaviors;

public static partial class DynamicProfileGrid
{
    private static readonly DependencyProperty ColumnFiltersStateProperty = DependencyProperty.RegisterAttached(
        "ColumnFiltersState", typeof(ColumnFiltersState), typeof(DynamicProfileGrid));
    private static readonly DependencyProperty HeaderFilterMarkerProperty = DependencyProperty.RegisterAttached(
        "HeaderFilterMarker", typeof(Ui.TextBlock), typeof(DynamicProfileGrid));
    private static readonly DependencyProperty OriginalFilterHeaderProperty = DependencyProperty.RegisterAttached(
        "OriginalFilterHeader", typeof(object), typeof(DynamicProfileGrid));
    internal static object? GetColumnHeaderContent(DataGridColumn column) => column.GetValue(OriginalFilterHeaderProperty) ?? column.Header;

    private sealed class ColumnFiltersState
    {
        public readonly Ui.Flyout Flyout = new() { Placement = PlacementMode.Bottom, Padding = new Thickness(16) };
        public CatalogViewModel? Owner;
        public EventHandler? Changed;
        public DataGridColumnHeader? PressedHeader;
        public Point Start;
        public bool Dragged;
        public CancellationTokenSource? EditorCancellation;
        public DispatcherOperation? HeaderPreparation;
    }

    private static void InitializeColumnFilters(DataGrid grid)
    {
        if (grid.GetValue(ColumnFiltersStateProperty) is ColumnFiltersState) return;
        var state = new ColumnFiltersState();
        grid.SetValue(ColumnFiltersStateProperty, state);
        void Attach()
        {
            if (!ReferenceEquals(state.Owner, grid.DataContext as CatalogViewModel))
            {
                state.Flyout.Hide(); state.EditorCancellation?.Cancel(); state.PressedHeader = null;
            }
            if (state.Owner is not null) state.Owner.ColumnQueryChanged -= state.Changed;
            state.Owner = grid.DataContext as CatalogViewModel;
            state.Changed = (_, _) => ScheduleFilterHeaders(grid, state);
            if (state.Owner is not null) state.Owner.ColumnQueryChanged += state.Changed;
            ScheduleFilterHeaders(grid, state);
        }
        grid.Loaded += (_, _) => Attach();
        grid.DataContextChanged += (_, _) => { if (grid.IsLoaded) Attach(); };
        grid.Unloaded += (_, _) =>
        {
            if (state.Owner is not null) state.Owner.ColumnQueryChanged -= state.Changed;
            state.Owner = null;
            state.Flyout.Hide();
            state.EditorCancellation?.Cancel();
            state.HeaderPreparation?.Abort(); state.HeaderPreparation = null;
        };
        ((INotifyCollectionChanged)grid.Columns).CollectionChanged += (_, _) =>
        {
            state.Flyout.Hide();
            state.EditorCancellation?.Cancel();
            ScheduleFilterHeaders(grid, state);
        };
        grid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            state.PressedHeader = Ancestor<DataGridColumnHeader>(source);
            if (Ancestor<ButtonBase>(source) is not null || Ancestor<Thumb>(source) is not null) state.PressedHeader = null;
            state.Start = e.GetPosition(grid); state.Dragged = false;
        }), true);
        grid.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (state.PressedHeader is null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(grid);
            if (Math.Abs(point.X - state.Start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(point.Y - state.Start.Y) >= SystemParameters.MinimumVerticalDragDistance) state.Dragged = true;
        }), true);
        grid.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            var header = state.PressedHeader;
            state.PressedHeader = null;
            if (header is null || state.Dragged || header != Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject)
                || grid.DataContext is not CatalogViewModel owner || Target(header.Column) is not { } target) return;
            e.Handled = true;
            _ = OpenColumnFlyoutAsync(grid, state, owner, header, target);
        }), true);
        grid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space or Key.F4)
                || Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null
                || Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header
                || grid.DataContext is not CatalogViewModel owner || Target(header.Column) is not { } target) return;
            e.Handled = true;
            _ = OpenColumnFlyoutAsync(grid, state, owner, header, target);
        }), true);
    }

    private static void ScheduleFilterHeaders(DataGrid grid, ColumnFiltersState state)
    {
        if (state.HeaderPreparation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing) return;
        // DataGridColumnHeadersPresenter receives Columns.CollectionChanged after
        // our earlier subscriber. Changing Header during Insert reenters its view
        // before the new index exists. Coalesce all inserts and decorate only
        // after the complete collection notification/rebuild has unwound.
        state.HeaderPreparation = grid.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            state.HeaderPreparation = null;
            if (!grid.IsLoaded || !ReferenceEquals(grid.GetValue(ColumnFiltersStateProperty), state)
                || !ReferenceEquals(state.Owner, grid.DataContext as CatalogViewModel)) return;
            foreach (var column in grid.Columns.ToArray())
                if (grid.Columns.Contains(column)) PrepareFilterHeader(grid, column);
            UpdateHeaderFilterMarkers(grid, state);
        }));
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static CatalogColumnTarget? Target(DataGridColumn? column) => column?.GetValue(FieldIdProperty) is string id
        ? id == StatusColumnId ? CatalogColumnTarget.Coverage : id == PathColumnId ? CatalogColumnTarget.FullPath : CatalogColumnTarget.Field(id)
        : null;

    private static void PrepareFilterHeader(DataGrid grid, DataGridColumn column)
    {
        if (grid.DataContext is CatalogViewModel && Target(column) is not null) column.CanUserSort = false;
        if (Target(column) is null || column.GetValue(HeaderFilterMarkerProperty) is Ui.TextBlock) return;
        var headerStyle = new Style(typeof(DataGridColumnHeader), column.HeaderStyle ?? grid.ColumnHeaderStyle
            ?? grid.TryFindResource(typeof(DataGridColumnHeader)) as Style);
        headerStyle.Setters.Add(new Setter(UIElement.FocusableProperty, true));
        headerStyle.Setters.Add(new Setter(KeyboardNavigation.IsTabStopProperty, true));
        column.HeaderStyle = headerStyle;
        column.SetValue(OriginalFilterHeaderProperty, column.Header);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (column.Header is UIElement element) panel.Children.Add(element);
        else panel.Children.Add(new Ui.TextBlock { Text = Convert.ToString(column.Header), VerticalAlignment = VerticalAlignment.Center });
        var marker = new Ui.TextBlock { Text = "⌄", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L(GetLocalizer(grid), "Loc.Catalog.ColumnFilter.Open.Help", "클릭하여 이 컬럼을 필터링하거나 정렬합니다.") };
        panel.Children.Add(marker);
        column.SetValue(HeaderFilterMarkerProperty, marker);
        column.Header = panel;
    }

    private static void UpdateHeaderFilterMarkers(DataGrid grid, ColumnFiltersState state)
    {
        foreach (var column in grid.Columns)
            if (column.GetValue(HeaderFilterMarkerProperty) is Ui.TextBlock marker && Target(column) is { } target)
            {
                var filtered = state.Owner?.GetColumnFilter(target) is not null;
                var sorted = state.Owner?.IsColumnSorted(target) == true;
                marker.Text = (filtered ? "● " : "") + (sorted ? state.Owner!.IsColumnSortDescending ? "↓" : "↑" : "⌄");
                marker.ToolTip = L(GetLocalizer(grid), "Loc.Catalog.ColumnFilter.Open.Help", "클릭하여 이 컬럼을 필터링하거나 정렬합니다.");
            }
    }

    private static async Task OpenColumnFlyoutAsync(DataGrid grid, ColumnFiltersState state, CatalogViewModel owner,
        DataGridColumnHeader header, CatalogColumnTarget target)
    {
        state.Flyout.Hide();
        state.EditorCancellation?.Cancel();
        state.EditorCancellation?.Dispose();
        state.EditorCancellation = new CancellationTokenSource();
        var token = state.EditorCancellation.Token;
        var localizer = GetLocalizer(grid) ?? owner.Localizer;
        string T(string key, string fallback) => localizer.Get("Loc.Catalog.ColumnFilter." + key, fallback);
        var field = target.Kind == CatalogColumnTargetKind.ProfileField
            ? owner.Fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, target.FieldId, StringComparison.OrdinalIgnoreCase)) : null;
        var kind = field?.Kind ?? (target.Kind == CatalogColumnTargetKind.Coverage ? ProfileFieldValueKind.Boolean : ProfileFieldValueKind.String);
        var filter = owner.GetColumnFilter(target);
        var body = new StackPanel { Width = 320 };
        var title = target.Kind == CatalogColumnTargetKind.FullPath ? localizer.Get("Loc.Catalog.Column.FolderPath", "폴더 경로")
            : target.Kind == CatalogColumnTargetKind.Coverage ? localizer.Get("Loc.Catalog.Column.Status", "상태") : field?.Header ?? target.FieldId ?? "";
        body.Children.Add(new Ui.TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
        var sorting = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        AddButton(sorting, T("Ascending", "오름차순"), () => owner.ApplyColumnSort(target, false));
        AddButton(sorting, T("Descending", "내림차순"), () => owner.ApplyColumnSort(target, true));
        AddButton(sorting, T("ClearSort", "정렬 해제"), () => owner.ApplyColumnSort(target, null));
        body.Children.Add(sorting);
        body.Children.Add(new Ui.TextBlock { Text = T("Condition", "필터 조건"), Margin = new Thickness(0, 0, 0, 4) });
        var choices = new List<FilterChoice> { new(null, T("All", "전체")) };
        if (target.Kind == CatalogColumnTargetKind.Coverage)
        {
            choices.Add(new(CatalogFilterOperator.CoverageEquals, localizer.Get("Loc.Common.Completed", "완료"), false));
            choices.Add(new(CatalogFilterOperator.CoverageEquals, localizer.Get("Loc.Common.PendingScope", "대기 범위"), true));
        }
        else if (kind == ProfileFieldValueKind.Boolean)
        {
            choices.Add(new(CatalogFilterOperator.BooleanEquals, T("True", "참"), true));
            choices.Add(new(CatalogFilterOperator.BooleanEquals, T("False", "거짓"), false));
        }
        else if (kind is ProfileFieldValueKind.Int32 or ProfileFieldValueKind.Decimal) choices.Add(new(CatalogFilterOperator.NumberRange, T("NumberRange", "숫자 범위")));
        else if (kind == ProfileFieldValueKind.DateTime) choices.Add(new(CatalogFilterOperator.DateRange, T("DateRange", "날짜 범위")));
        else
        {
            choices.Add(new(CatalogFilterOperator.Contains, T("Contains", "포함")));
            choices.Add(new(CatalogFilterOperator.DoesNotContain, T("NotContains", "포함하지 않음")));
            choices.Add(new(CatalogFilterOperator.IsEmpty, T("Empty", "빈 텍스트")));
            choices.Add(new(CatalogFilterOperator.IsNotEmpty, T("NotEmpty", "비어 있지 않은 텍스트")));
        }
        if (field?.IsNullable == true)
        {
            choices.Add(new(CatalogFilterOperator.IsMissing, T("Missing", "값 없음")));
            choices.Add(new(CatalogFilterOperator.HasValue, T("HasValue", "값 있음")));
        }
        var mode = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(FilterChoice.Label), MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 12) };
        mode.SelectedItem = choices.FirstOrDefault(choice => choice.Operator == filter?.Operator && choice.Boolean == filter?.Boolean) ?? choices[0];
        body.Children.Add(mode);
        var editors = new StackPanel(); body.Children.Add(editors);
        var error = new Ui.TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCriticalBrush");
        body.Children.Add(error);
        body.Children.Add(new Ui.TextBlock { Text = T("Automatic.Help", "입력을 멈추면 자동 적용됩니다. 여러 컬럼의 조건은 모두 만족해야 합니다."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Opacity = 0.7 });
        void Apply(CatalogColumnFilter? value)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (value is null) owner.ClearColumnFilter(target); else owner.ApplyColumnFilter(value);
                error.Visibility = Visibility.Collapsed;
            }
            catch (ArgumentException) { Invalid(); }
        }
        void Invalid()
        {
            error.Text = T("InvalidRange", "범위를 확인하세요. 시작값은 종료값보다 클 수 없습니다.");
            error.Visibility = Visibility.Visible;
        }
        CatalogNumericRange? range = null;
        async Task BuildEditor()
        {
            editors.Children.Clear(); error.Visibility = Visibility.Collapsed;
            var selected = (FilterChoice)mode.SelectedItem;
            if (selected.Operator is null) { Apply(null); return; }
            var draft = new CatalogColumnFilter(target, selected.Operator.Value) { Boolean = selected.Boolean };
            if (selected.Operator is CatalogFilterOperator.Contains or CatalogFilterOperator.DoesNotContain)
            {
                var text = new Ui.TextBox { Text = filter?.Text ?? "", PlaceholderText = T("Keyword", "찾을 텍스트"), ClearButtonEnabled = true };
                editors.Children.Add(text);
                text.TextChanged += (_, _) => Apply(text.Text.Length == 0 ? null : draft with { Text = text.Text });
                if (filter?.Operator != selected.Operator) Apply(text.Text.Length == 0 ? null : draft with { Text = text.Text });
                return;
            }
            if (selected.Operator == CatalogFilterOperator.DateRange)
            {
                var dates = new CalendarRangeDraft(filter?.StartDate, filter?.EndDate);
                var start = new Ui.CalendarDatePicker { FirstDayOfWeek = localizer.Culture.DateTimeFormat.FirstDayOfWeek };
                var end = new Ui.CalendarDatePicker { FirstDayOfWeek = localizer.Culture.DateTimeFormat.FirstDayOfWeek };
                start.SetBinding(Ui.CalendarDatePicker.DateProperty, new Binding(nameof(CalendarRangeDraft.Start)) { Source = dates, Mode = BindingMode.TwoWay });
                end.SetBinding(Ui.CalendarDatePicker.DateProperty, new Binding(nameof(CalendarRangeDraft.End)) { Source = dates, Mode = BindingMode.TwoWay });
                AddLabel(editors, T("StartDate", "시작일"), start); AddLabel(editors, T("EndDate", "종료일"), end);
                dates.Changed = () => Apply(draft with { StartDate = dates.Start, EndDate = dates.End });
                return;
            }
            if (selected.Operator == CatalogFilterOperator.NumberRange)
            {
                var minimum = new Ui.TextBox { Text = filter?.Minimum?.ToString(localizer.Culture) ?? "", PlaceholderText = T("NoLowerBound", "최소 제한 없음") };
                var maximum = new Ui.TextBox { Text = filter?.Maximum?.ToString(localizer.Culture) ?? "", PlaceholderText = T("NoUpperBound", "최대 제한 없음") };
                var lower = new Slider { Minimum = 0, Maximum = 10000, IsEnabled = false };
                var upper = new Slider { Minimum = 0, Maximum = 10000, Value = 10000, IsEnabled = false };
                AddLabel(editors, T("Minimum", "최소값"), minimum); editors.Children.Add(lower);
                AddLabel(editors, T("Maximum", "최대값"), maximum); editors.Children.Add(upper);
                bool synchronizing = false;
                void Changed()
                {
                    if (synchronizing) return;
                    if (!TryDecimal(minimum.Text, out var min) || !TryDecimal(maximum.Text, out var max)) { Invalid(); return; }
                    Apply(min is null && max is null ? null : draft with { Minimum = min, Maximum = max });
                    if (range is not null)
                    {
                        synchronizing = true;
                        lower.Value = Position(min ?? range.Minimum); upper.Value = Position(max ?? range.Maximum);
                        synchronizing = false;
                    }
                }
                bool TryDecimal(string text, out decimal? value)
                {
                    value = null;
                    if (string.IsNullOrWhiteSpace(text)) return true;
                    if (!decimal.TryParse(text, NumberStyles.Number, localizer.Culture, out var parsed)) return false;
                    value = parsed; return true;
                }
                double Position(decimal value) => range is null ? 0 : CatalogNumericRangeMath.Position(range, value) * 10000;
                decimal FromPosition(double position)
                {
                    var value = CatalogNumericRangeMath.Interpolate(range!, (int)Math.Round(position));
                    return kind == ProfileFieldValueKind.Int32 ? decimal.Round(value, 0) : value;
                }
                minimum.TextChanged += (_, _) => Changed(); maximum.TextChanged += (_, _) => Changed();
                lower.ValueChanged += (_, _) => { if (!synchronizing && range is not null) minimum.Text = FromPosition(lower.Value).ToString(localizer.Culture); };
                upper.ValueChanged += (_, _) => { if (!synchronizing && range is not null) maximum.Text = FromPosition(upper.Value).ToString(localizer.Culture); };
                try
                {
                    range ??= await owner.ReadColumnNumericRangeAsync(target.FieldId!, token);
                    if (token.IsCancellationRequested || !Equals(mode.SelectedItem, selected) || !editors.Children.Contains(lower)) return;
                    lower.IsEnabled = upper.IsEnabled = range is not null && range.Minimum != range.Maximum;
                    synchronizing = true;
                    lower.Value = Position(filter?.Minimum ?? range?.Minimum ?? 0); upper.Value = Position(filter?.Maximum ?? range?.Maximum ?? 0);
                    synchronizing = false;
                }
                catch (OperationCanceledException) { }
                catch (Exception) { error.Text = T("RangeUnavailable", "범위를 불러오지 못했습니다. 숫자를 직접 입력할 수 있습니다."); error.Visibility = Visibility.Visible; }
                return;
            }
            Apply(draft);
        }
        mode.SelectionChanged += (_, _) => { filter = owner.GetColumnFilter(target); _ = BuildEditor(); };
        state.Flyout.Content = body;
        if (grid.TryFindResource(typeof(Ui.Flyout)) is Style flyoutStyle) state.Flyout.Style = flyoutStyle;
        state.Flyout.ApplyTemplate();
        if (state.Flyout.Template?.FindName("PART_Popup", state.Flyout) is Popup popup)
        {
            popup.PlacementTarget = header;
            popup.StaysOpen = false;
            state.Flyout.Show();
            await BuildEditor();
        }
    }

    private sealed record FilterChoice(CatalogFilterOperator? Operator, string Label, bool? Boolean = null);
    private sealed class CalendarRangeDraft(DateTime? start, DateTime? end) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private DateTime? _start = start;
        private DateTime? _end = end;
        public Action? Changed { get; set; }
        public DateTime? Start { get => _start; set { if (SetProperty(ref _start, value)) Changed?.Invoke(); } }
        public DateTime? End { get => _end; set { if (SetProperty(ref _end, value)) Changed?.Invoke(); } }
    }
    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Ui.Button { Content = label, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0), MinWidth = 0 };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private static void AddLabel(Panel panel, string label, UIElement control)
    {
        panel.Children.Add(new Ui.TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(control);
    }
}
