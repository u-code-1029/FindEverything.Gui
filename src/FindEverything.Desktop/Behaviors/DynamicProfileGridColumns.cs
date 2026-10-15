using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Behaviors;

public static partial class DynamicProfileGrid
{
    private static void UpdateFrozenColumns(DataGrid grid)
    {
        if (grid.Columns.Count <= SelectionColumnOffset) return;
        var identity = grid.Columns[SelectionColumnOffset];
        var identityWidth = identity.Width.IsAbsolute ? identity.Width.Value : identity.ActualWidth;
        // A pinned identifying column must not consume the scrolling viewport.
        // Restore it automatically as soon as a wider window permits it.
        grid.FrozenColumnCount = identity.DisplayIndex == SelectionColumnOffset && grid.ActualWidth >= 112 + identityWidth + 160
            ? SelectionColumnOffset + 1 : SelectionColumnOffset;
    }

    public static void ShowColumnsMenu(DataGrid grid, FrameworkElement anchor)
    {
        if (!grid.IsEnabled) return;
        if (anchor.ContextMenu is { IsOpen: true } previous) previous.IsOpen = false;
        var localizer = GetLocalizer(grid);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var fields = GetFields(grid)?.OfType<ProfileFieldDescriptor>().ToDictionary(f => f.FieldId)
            ?? new Dictionary<string, ProfileFieldDescriptor>();
        foreach (var column in grid.Columns.Skip(SelectionColumnOffset))
        {
            var id = (string)column.GetValue(FieldIdProperty);
            var title = fields.TryGetValue(id, out var field) ? field.Header : L(localizer, "Loc.Catalog.Column.FolderPath", "폴더 경로");
            var item = new Ui.MenuItem { Header = title, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true, IsEnabled = column != grid.Columns[SelectionColumnOffset] };
            if (!item.IsEnabled) item.ToolTip = L(localizer, "Loc.Ux.Results.Columns.Identity", "첫 번째 식별 컬럼은 항상 표시됩니다.");
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveLayout(grid);
                ScheduleSizing(grid);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var restore = new Ui.MenuItem { Header = L(localizer, "Loc.Ux.Results.Columns.Restore", "모든 컬럼 표시 · 너비 초기화") };
        restore.Click += (_, _) =>
        {
            foreach (var column in grid.Columns.Skip(SelectionColumnOffset))
            {
                column.Visibility = Visibility.Visible;
                column.SetValue(IsManualWidthProperty, false);
            }
            SaveLayout(grid);
            ScheduleSizing(grid);
        };
        menu.Items.Add(restore);
        if (grid.DataContext is CatalogViewModel owner)
        {
            menu.Items.Add(new Separator());
            var original = new Ui.MenuItem { IsCheckable = true, StaysOpenOnClick = true,
                Header = L(localizer, "Loc.Ux.Results.Original.DisplayOnly", "원본 값 표시 (화면만)"),
                ToolTip = L(localizer, "Loc.Ux.Results.Original.Help", "검색·정렬·복사에는 매핑된 값을 사용합니다. 셀의 텍스트를 직접 드래그해 복사하면 화면에 보이는 값이 복사됩니다.") };
            original.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(CatalogViewModel.ShowOriginalValues)) { Source = owner, Mode = BindingMode.TwoWay });
            menu.Items.Add(original);
        }
        DependencyPropertyChangedEventHandler enabledChanged = (_, _) => { if (!grid.IsEnabled) menu.IsOpen = false; };
        DependencyPropertyChangedEventHandler contextChanged = (_, _) => menu.IsOpen = false;
        NotifyCollectionChangedEventHandler columnsChanged = (_, _) => menu.IsOpen = false;
        RoutedEventHandler unloaded = (_, _) => menu.IsOpen = false;
        grid.IsEnabledChanged += enabledChanged;
        grid.DataContextChanged += contextChanged;
        grid.Unloaded += unloaded;
        ((INotifyCollectionChanged)grid.Columns).CollectionChanged += columnsChanged;
        menu.Closed += (_, _) =>
        {
            grid.IsEnabledChanged -= enabledChanged;
            grid.DataContextChanged -= contextChanged;
            grid.Unloaded -= unloaded;
            ((INotifyCollectionChanged)grid.Columns).CollectionChanged -= columnsChanged;
            if (ReferenceEquals(anchor.ContextMenu, menu)) anchor.ContextMenu = null;
        };
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
