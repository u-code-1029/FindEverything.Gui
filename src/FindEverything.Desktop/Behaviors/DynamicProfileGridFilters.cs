using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
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
    private static readonly DependencyProperty HeaderFilterButtonProperty = DependencyProperty.RegisterAttached(
        "HeaderFilterButton", typeof(Ui.Button), typeof(DynamicProfileGrid));
    private static readonly DependencyProperty OriginalFilterHeaderProperty = DependencyProperty.RegisterAttached(
        "OriginalFilterHeader", typeof(object), typeof(DynamicProfileGrid));
    internal static object? GetColumnHeaderContent(DataGridColumn column) => column.GetValue(OriginalFilterHeaderProperty) ?? column.Header;

    private sealed class ColumnFiltersState
    {
        public readonly Ui.Flyout Flyout = new()
        {
            Placement = PlacementMode.Bottom, Padding = new Thickness(16),
            Width = 0, Height = 0, MinWidth = 0, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Focusable = false
        };
        public CatalogViewModel? Owner;
        public EventHandler? Changed;
        public DataGridColumnHeader? PressedHeader;
        public Point Start;
        public bool Dragged;
        public CancellationTokenSource? EditorCancellation;
        public DispatcherOperation? HeaderPreparation;
        public DispatcherOperation? HostUpdate;
        public FrameworkElement? FocusOrigin;
        public Ui.CalendarDatePicker? ActiveCalendar;
        public Panel? HostPanel;
        public AdornerLayer? HostAdornerLayer;
        public ColumnFlyoutAdorner? HostAdorner;
    }

    private static void InitializeColumnFilters(DataGrid grid)
    {
        if (grid.GetValue(ColumnFiltersStateProperty) is ColumnFiltersState) return;
        var state = new ColumnFiltersState();
        grid.SetValue(ColumnFiltersStateProperty, state);
        state.Flyout.Closed += (_, _) => { CloseActiveCalendar(state); state.EditorCancellation?.Cancel(); };
        state.Flyout.KeyDown += (_, e) =>
        {
            // Let a ComboBox/calendar close its own nested popup first. Only
            // an unhandled Escape bubbling from the outer editor dismisses it.
            if (e.Key != Key.Escape || !state.Flyout.IsOpen) return;
            e.Handled = true;
            if (state.ActiveCalendar is { IsCalendarOpen: true } calendar)
            {
                calendar.SetCurrentValue(Ui.CalendarDatePicker.IsCalendarOpenProperty, false);
                calendar.Focus();
                return;
            }
            state.Flyout.Hide();
            if (state.FocusOrigin is { IsLoaded: true, IsVisible: true } origin) origin.Focus();
        };
        void ReconcileHost()
        {
            var next = grid.IsLoaded ? grid.DataContext as CatalogViewModel : null;
            if (!ReferenceEquals(state.Owner, next))
            {
                if (state.Owner is not null) state.Owner.ColumnQueryChanged -= state.Changed;
                DetachFlyoutHost(state);
                state.Owner = next;
                state.Changed = (_, _) => ScheduleFilterHeaders(grid, state);
                if (next is not null) next.ColumnQueryChanged += state.Changed;
            }
            if (state.Owner is not null) EnsureFlyoutHost(grid, state);
            if (grid.IsLoaded) ScheduleFilterHeaders(grid, state);
        }
        void ScheduleHost()
        {
            // DataContext inheritance and Loaded/Unloaded walk logical children.
            // Changing an ancestor's Children inside that walk throws. Perform
            // both insertion and removal after the event has completely unwound.
            if (state.HostUpdate?.Status == DispatcherOperationStatus.Pending) return;
            state.HostUpdate = grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                state.HostUpdate = null;
                ReconcileHost();
            }));
        }
        grid.Loaded += (_, _) => ScheduleHost();
        grid.DataContextChanged += (_, _) => ScheduleHost();
        grid.IsEnabledChanged += (_, _) =>
        {
            if (grid.IsEnabled) return;
            // Popup windows are hosted outside the disabled grid's visual
            // subtree. Dismiss both levels when scanning locks interaction.
            CloseActiveCalendar(state);
            state.Flyout.Hide();
            state.EditorCancellation?.Cancel();
            state.PressedHeader = null;
        };
        grid.Unloaded += (_, _) =>
        {
            state.EditorCancellation?.Cancel();
            state.PressedHeader = null;
            ScheduleHost();
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
            if (state.PressedHeader is { } header && IsNestedHeaderControl(source, header)) state.PressedHeader = null;
            state.Start = e.GetPosition(grid); state.Dragged = false;
        }), true);
        grid.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (state.PressedHeader is null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(grid);
            if (ExceedsHeaderClickDistance(state.Start, point)) state.Dragged = true;
        }), true);
        grid.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            var header = state.PressedHeader;
            state.PressedHeader = null;
            var source = e.OriginalSource as DependencyObject;
            if (header is null || state.Dragged || ExceedsHeaderClickDistance(state.Start, e.GetPosition(grid))
                || IsNestedHeaderControl(source, header) || header != Ancestor<DataGridColumnHeader>(source)
                || grid.DataContext is not CatalogViewModel owner || Target(header.Column) is not { } target) return;
            e.Handled = true;
            // DataGridColumnHeader derives from ButtonBase and captures the
            // pointer on MouseDown. Handling PreviewMouseUp bypasses its native
            // MouseUp/ReleaseMouseCapture. Release only this header's capture
            // before sorting; otherwise the header can keep receiving clicks
            // intended for the result cells or a subsequently opened filter.
            if (header.IsMouseCaptured) header.ReleaseMouseCapture();
            ToggleHeaderSort(owner, target);
        }), true);
        grid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space or Key.F4)
                || Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header
                || IsNestedHeaderControl(e.OriginalSource as DependencyObject, header)
                || grid.DataContext is not CatalogViewModel owner || Target(header.Column) is not { } target) return;
            e.Handled = true;
            if (e.Key == Key.F4) _ = OpenColumnFlyoutAsync(grid, state, owner, header, target);
            else ToggleHeaderSort(owner, target);
        }), true);
        if (grid.IsLoaded) ScheduleHost();
    }

    private static void ToggleHeaderSort(CatalogViewModel owner, CatalogColumnTarget target) =>
        owner.ApplyColumnSort(target, owner.IsColumnSorted(target) && !owner.IsColumnSortDescending);

    /// <summary>Filter chips edit their condition; only their separate dismiss button removes it.</summary>
    public static void OpenColumnFilter(DataGrid grid, CatalogColumnTarget target, FrameworkElement anchor)
    {
        if (!grid.IsEnabled || grid.DataContext is not CatalogViewModel owner
            || grid.GetValue(ColumnFiltersStateProperty) is not ColumnFiltersState state) return;
        _ = OpenColumnFlyoutAsync(grid, state, owner, anchor, target);
    }

    private static bool EnsureFlyoutHost(DataGrid grid, ColumnFiltersState state)
    {
        if (!grid.IsLoaded) return false;
        if (state.HostPanel?.Children.Contains(state.Flyout) == true || state.HostAdorner is not null) return true;
        state.Flyout.DataContext = state.Owner;
        // A detached Flyout's template Popup has a visual parent but is never
        // Loaded, so WPF coerces IsOpen back to false. Keep its zero-sized owner
        // in the loaded tree; popup content still measures independently in its
        // own HWND. Do not disable inherited hit testing on that owner.
        for (var parent = VisualTreeHelper.GetParent(grid); parent is not null; parent = ParentOf(parent))
        {
            if (parent is not Panel { IsItemsHost: false } panel
                || panel is not (Grid or Canvas or StackPanel or DockPanel)) continue;
            // Insert first so a DockPanel's existing LastChildFill is unchanged.
            panel.Children.Insert(0, state.Flyout);
            state.HostPanel = panel;
            return true;
        }
        if (AdornerLayer.GetAdornerLayer(grid) is not { } layer) return false;
        var adorner = new ColumnFlyoutAdorner(grid, state.Flyout);
        layer.Add(adorner);
        state.HostAdorner = adorner; state.HostAdornerLayer = layer;
        return true;
    }

    private static void DetachFlyoutHost(ColumnFiltersState state)
    {
        CloseActiveCalendar(state);
        state.Flyout.Hide();
        state.EditorCancellation?.Cancel(); state.EditorCancellation?.Dispose(); state.EditorCancellation = null;
        state.PressedHeader = null;
        state.FocusOrigin = null;
        state.Flyout.Content = null;
        state.HostPanel?.Children.Remove(state.Flyout); state.HostPanel = null;
        if (state.HostAdorner is { } adorner)
        {
            state.HostAdornerLayer?.Remove(adorner);
            adorner.Detach();
        }
        state.HostAdorner = null; state.HostAdornerLayer = null;
        state.Flyout.ClearValue(FrameworkElement.DataContextProperty);
    }

    private static void CloseActiveCalendar(ColumnFiltersState state)
    {
        state.ActiveCalendar?.SetCurrentValue(Ui.CalendarDatePicker.IsCalendarOpenProperty, false);
        state.ActiveCalendar = null;
    }

    private sealed class ColumnFlyoutAdorner : Adorner
    {
        private Ui.Flyout? _flyout;
        public ColumnFlyoutAdorner(UIElement element, Ui.Flyout flyout) : base(element)
        {
            _flyout = flyout; AddVisualChild(flyout); AddLogicalChild(flyout);
        }
        protected override int VisualChildrenCount => _flyout is null ? 0 : 1;
        protected override Visual GetVisualChild(int index) => index == 0 && _flyout is not null
            ? _flyout : throw new ArgumentOutOfRangeException(nameof(index));
        protected override Size MeasureOverride(Size constraint)
        {
            _flyout?.Measure(new Size()); return new Size();
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            _flyout?.Arrange(new Rect()); return finalSize;
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        public void Detach()
        {
            if (_flyout is not { } child) return;
            RemoveVisualChild(child); RemoveLogicalChild(child); _flyout = null;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static bool IsNestedHeaderControl(DependencyObject? source, DataGridColumnHeader header) =>
        // DataGridColumnHeader itself inherits ButtonBase. Only its embedded
        // controls (for example the text-column refresh button) own their input.
        (Ancestor<ButtonBase>(source) is { } button && !ReferenceEquals(button, header))
        || Ancestor<Thumb>(source) is not null;

    private static bool ExceedsHeaderClickDistance(Point start, Point end) =>
        Math.Abs(end.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(end.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

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
        var originalHeader = column.Header;
        column.SetValue(OriginalFilterHeaderProperty, originalHeader);
        // Decorate before the column joins the grid. Its refresh button has no
        // visual parent yet, so constructing the header never forces layout.
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (originalHeader is UIElement element) panel.Children.Add(element);
        else panel.Children.Add(new Ui.TextBlock { Text = Convert.ToString(originalHeader), VerticalAlignment = VerticalAlignment.Center });
        var marker = new Ui.TextBlock { Text = "", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(marker);
        var filterButton = new Ui.Button
        {
            Width = 28, Height = 28, MinWidth = 28, MinHeight = 28, Padding = new Thickness(4),
            Margin = new Thickness(4, 0, 0, 0), Appearance = Ui.ControlAppearance.Transparent,
            Icon = new Ui.SymbolIcon(Ui.SymbolRegular.Filter20),
            ToolTip = L(GetLocalizer(grid), "Loc.Ux.Results.ColumnFilter", "컬럼 필터 (F4)")
        };
        var headerName = GetFields(grid)?.OfType<ProfileFieldDescriptor>()
            .FirstOrDefault(field => field.FieldId == (string?)column.GetValue(FieldIdProperty))?.Header
            ?? Convert.ToString(originalHeader);
        System.Windows.Automation.AutomationProperties.SetName(filterButton,
            headerName + " · " + L(GetLocalizer(grid), "Loc.Ux.Results.ColumnFilter", "컬럼 필터 (F4)"));
        filterButton.Click += (_, e) =>
        {
            e.Handled = true;
            if (Target(column) is { } target)
                OpenColumnFilter(grid, target, (FrameworkElement?)Ancestor<DataGridColumnHeader>(filterButton) ?? filterButton);
        };
        panel.Children.Add(filterButton);
        column.SetValue(HeaderFilterMarkerProperty, marker);
        column.SetValue(HeaderFilterButtonProperty, filterButton);
        column.SetCurrentValue(DataGridColumn.HeaderProperty, panel);
    }

    private static void UpdateHeaderFilterMarkers(DataGrid grid, ColumnFiltersState state)
    {
        foreach (var column in grid.Columns)
            if (column.GetValue(HeaderFilterMarkerProperty) is Ui.TextBlock marker && Target(column) is { } target)
            {
                var filtered = state.Owner?.GetColumnFilter(target) is not null;
                var sorted = state.Owner?.IsColumnSorted(target) == true;
                marker.Text = sorted ? state.Owner!.IsColumnSortDescending ? "↓" : "↑" : "";
                if (column.GetValue(HeaderFilterButtonProperty) is Ui.Button button)
                {
                    button.Appearance = filtered ? Ui.ControlAppearance.Secondary : Ui.ControlAppearance.Transparent;
                    button.ToolTip = L(GetLocalizer(grid), filtered ? "Loc.Ux.Results.EditFilter" : "Loc.Ux.Results.ColumnFilter",
                        filtered ? "적용된 필터 수정 (F4)" : "컬럼 필터 (F4)");
                }
            }
    }

    private static async Task OpenColumnFlyoutAsync(DataGrid grid, ColumnFiltersState state, CatalogViewModel owner,
        FrameworkElement header, CatalogColumnTarget target)
    {
        if (!grid.IsEnabled || !ReferenceEquals(state.Owner, owner) || !EnsureFlyoutHost(grid, state)) return;
        CloseActiveCalendar(state);
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
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
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
        var mode = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(FilterChoice.Label),
            Margin = new Thickness(0, 0, 0, 12) };
        mode.SetResourceReference(FrameworkElement.StyleProperty, "ContentAwareComboBoxStyle");
        mode.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CatalogViewModel.CanEditResultFilters)) { Source = owner });
        mode.SelectedItem = choices.FirstOrDefault(choice => choice.Operator == filter?.Operator && choice.Boolean == filter?.Boolean) ?? choices[0];
        body.Children.Add(mode);
        var editors = new StackPanel(); body.Children.Add(editors);
        editors.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CatalogViewModel.CanEditResultFilters)) { Source = owner });
        var error = new Ui.TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCriticalBrush");
        body.Children.Add(error);
        body.Children.Add(new Ui.TextBlock { Text = owner.IsShowingSelectedOnly
            ? T("SelectedOnly", "선택 항목 검토 중에는 필터를 변경하지 않습니다. 검토를 닫으면 기존 검색 조건으로 돌아갑니다.")
            : T("Automatic.Help", "입력을 멈추면 자동 적용됩니다. 여러 컬럼의 조건은 모두 만족해야 합니다."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Opacity = 0.7 });
        void Apply(CatalogColumnFilter? value)
        {
            if (token.IsCancellationRequested || !owner.CanEditResultFilters) return;
            try
            {
                if (value is null) owner.ClearColumnFilter(target); else owner.ApplyColumnFilter(value, debounce: true);
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
            CloseActiveCalendar(state);
            editors.Children.Clear(); error.Visibility = Visibility.Collapsed;
            if (owner.IsShowingSelectedOnly) return;
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
                var dates = new CalendarRangeDraft(filter?.StartDate, filter?.EndDate, localizer.Culture,
                    localizer.Get("Loc.Ux.Results.Date.Unbounded", "제한 없음"));
                var start = new Ui.CalendarDatePicker { FirstDayOfWeek = localizer.Culture.DateTimeFormat.FirstDayOfWeek };
                var end = new Ui.CalendarDatePicker { FirstDayOfWeek = localizer.Culture.DateTimeFormat.FirstDayOfWeek };
                start.Click += (_, _) => state.ActiveCalendar = start;
                end.Click += (_, _) => state.ActiveCalendar = end;
                start.SetBinding(Ui.CalendarDatePicker.DateProperty, new Binding(nameof(CalendarRangeDraft.Start)) { Source = dates, Mode = BindingMode.TwoWay });
                end.SetBinding(Ui.CalendarDatePicker.DateProperty, new Binding(nameof(CalendarRangeDraft.End)) { Source = dates, Mode = BindingMode.TwoWay });
                start.SetBinding(ContentControl.ContentProperty, new Binding(nameof(CalendarRangeDraft.StartLabel)) { Source = dates });
                end.SetBinding(ContentControl.ContentProperty, new Binding(nameof(CalendarRangeDraft.EndLabel)) { Source = dates });
                AddDate(T("StartDate", "시작일"), start, () => dates.Start = null);
                AddDate(T("EndDate", "종료일"), end, () => dates.End = null);
                editors.Children.Add(new Ui.TextBlock { Text = localizer.Get("Loc.Ux.Results.Date.Inclusive", "시작일과 종료일을 모두 포함합니다. 한쪽만 지정할 수도 있습니다."),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                dates.Changed = () => Apply(dates.Start is null && dates.End is null ? null : draft with { StartDate = dates.Start, EndDate = dates.End });
                void AddDate(string label, Ui.CalendarDatePicker picker, Action clear)
                {
                    var row = new DockPanel();
                    var clearButton = new Ui.Button { Width = 32, Height = 32, MinWidth = 32, Padding = new Thickness(4),
                        Margin = new Thickness(8, 0, 0, 0), Appearance = Ui.ControlAppearance.Transparent,
                        Icon = new Ui.SymbolIcon(Ui.SymbolRegular.Dismiss16), ToolTip = localizer.Get("Loc.Ux.Results.Date.Clear", "날짜 제한 해제") };
                    System.Windows.Automation.AutomationProperties.SetName(clearButton, label + " · " + clearButton.ToolTip);
                    clearButton.Click += (_, _) => clear();
                    DockPanel.SetDock(clearButton, Dock.Right); row.Children.Add(clearButton);
                    picker.HorizontalAlignment = HorizontalAlignment.Stretch;
                    System.Windows.Automation.AutomationProperties.SetName(picker, label);
                    row.Children.Add(picker); AddLabel(editors, label, row);
                }
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
                catch (Exception)
                {
                    if (token.IsCancellationRequested || !Equals(mode.SelectedItem, selected) || !editors.Children.Contains(lower)) return;
                    error.Text = T("RangeUnavailable", "범위를 불러오지 못했습니다. 숫자를 직접 입력할 수 있습니다.");
                    error.Visibility = Visibility.Visible;
                }
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
            state.FocusOrigin = header;
            state.Flyout.Show();
            // Popup HWND creation is queued. Focusing its real first control
            // after opening gives keyboard users the same interactive editor.
            _ = grid.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!token.IsCancellationRequested && state.Flyout.IsOpen && mode.IsLoaded) mode.Focus();
            }));
            await BuildEditor();
        }
    }

    private sealed record FilterChoice(CatalogFilterOperator? Operator, string Label, bool? Boolean = null);
    private sealed class CalendarRangeDraft(DateTime? start, DateTime? end, CultureInfo culture, string unbounded) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private DateTime? _start = start;
        private DateTime? _end = end;
        public Action? Changed { get; set; }
        public string StartLabel => Start?.ToString("d", culture) ?? unbounded;
        public string EndLabel => End?.ToString("d", culture) ?? unbounded;
        public DateTime? Start { get => _start; set { if (SetProperty(ref _start, value)) { OnPropertyChanged(nameof(StartLabel)); Changed?.Invoke(); } } }
        public DateTime? End { get => _end; set { if (SetProperty(ref _end, value)) { OnPropertyChanged(nameof(EndLabel)); Changed?.Invoke(); } } }
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
