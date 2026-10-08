using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Behaviors;

public static class DynamicProfileGrid
{
    private const int SelectionColumnOffset = 1;

    public static readonly DependencyProperty FieldsProperty = DependencyProperty.RegisterAttached(
        "Fields",
        typeof(IEnumerable),
        typeof(DynamicProfileGrid),
        new PropertyMetadata(null, OnFieldsChanged));

    public static readonly DependencyProperty ProfileIdProperty = DependencyProperty.RegisterAttached(
        "ProfileId",
        typeof(string),
        typeof(DynamicProfileGrid),
        new PropertyMetadata(null, OnLayoutContextChanged));

    public static readonly DependencyProperty LayoutStoreProperty = DependencyProperty.RegisterAttached(
        "LayoutStore",
        typeof(IGridLayoutStore),
        typeof(DynamicProfileGrid),
        new PropertyMetadata(null, OnLayoutContextChanged));

    public static readonly DependencyProperty LocalizerProperty = DependencyProperty.RegisterAttached(
        "Localizer",
        typeof(IAppLocalizer),
        typeof(DynamicProfileGrid),
        new PropertyMetadata(null, OnLocalizerChanged));

    private static readonly DependencyProperty FieldIdProperty = DependencyProperty.RegisterAttached(
        "FieldId",
        typeof(string),
        typeof(DynamicProfileGrid));

    private static readonly DependencyProperty IsApplyingLayoutProperty = DependencyProperty.RegisterAttached(
        "IsApplyingLayout",
        typeof(bool),
        typeof(DynamicProfileGrid));

    public static void SetFields(DependencyObject element, IEnumerable? value) =>
        element.SetValue(FieldsProperty, value);

    public static IEnumerable? GetFields(DependencyObject element) =>
        (IEnumerable?)element.GetValue(FieldsProperty);

    public static void SetProfileId(DependencyObject element, string? value) =>
        element.SetValue(ProfileIdProperty, value);

    public static string? GetProfileId(DependencyObject element) =>
        (string?)element.GetValue(ProfileIdProperty);

    public static void SetLayoutStore(DependencyObject element, IGridLayoutStore? value) =>
        element.SetValue(LayoutStoreProperty, value);

    public static IGridLayoutStore? GetLayoutStore(DependencyObject element) =>
        (IGridLayoutStore?)element.GetValue(LayoutStoreProperty);

    public static void SetLocalizer(DependencyObject element, IAppLocalizer? value) =>
        element.SetValue(LocalizerProperty, value);

    public static IAppLocalizer? GetLocalizer(DependencyObject element) =>
        (IAppLocalizer?)element.GetValue(LocalizerProperty);

    private static void OnFieldsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not DataGrid dataGrid)
        {
            return;
        }

        RebuildColumns(dataGrid);
    }

    private static void OnLocalizerChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is DataGrid dataGrid)
        {
            RebuildColumns(dataGrid);
        }
    }

    private static void RebuildColumns(DataGrid dataGrid)
    {
        var localizer = GetLocalizer(dataGrid);
        dataGrid.FrozenColumnCount = 0;
        dataGrid.Columns.Clear();
        dataGrid.ColumnReordered -= OnColumnReordered;
        dataGrid.ColumnReordered += OnColumnReordered;
        dataGrid.PreviewMouseLeftButtonUp -= OnGridMouseLeftButtonUp;
        dataGrid.PreviewMouseLeftButtonUp += OnGridMouseLeftButtonUp;
        dataGrid.Columns.Add(new SelectionCheckBoxColumn(localizer));
        dataGrid.FrozenColumnCount = SelectionColumnOffset;
        dataGrid.Columns.Add(new HighlightingTextColumn
        {
            Header = L(localizer, "Loc.Catalog.Column.Status", "상태"),
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.CoverageText)),
            IsReadOnly = true,
            Width = DataGridLength.Auto,
            MinWidth = 84,
            MaxWidth = 140,
        });

        if (GetFields(dataGrid) is IEnumerable fields)
        {
            foreach (var field in fields.OfType<ProfileFieldDescriptor>().OrderBy(static field => field.Order))
            {
                var column = new MappedHighlightingTextColumn(field.FieldId)
                {
                    Header = field.Header,
                    IsReadOnly = true,
                    SortMemberPath = $"DisplayValues[{field.FieldId}]",
                    Width = DataGridLength.Auto,
                    MinWidth = 120,
                    MaxWidth = 320,
                };
                column.SetValue(FieldIdProperty, field.FieldId);
                dataGrid.Columns.Add(column);
            }
        }

        dataGrid.Columns.Add(new HighlightingTextColumn
        {
            Header = L(localizer, "Loc.Catalog.Column.FolderPath", "폴더 경로"),
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.FullPath)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 240,
        });

        ApplyLayout(dataGrid);
    }

    private static string L(
        IAppLocalizer? localizer,
        string key,
        string koreanFallback) =>
        localizer?.Get(key, koreanFallback) ?? koreanFallback;

    private static void OnLayoutContextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is DataGrid dataGrid)
        {
            ApplyLayout(dataGrid);
        }
    }

    private static void ApplyLayout(DataGrid dataGrid)
    {
        var profileId = GetProfileId(dataGrid);
        var store = GetLayoutStore(dataGrid);
        if (string.IsNullOrWhiteSpace(profileId) || store is null || dataGrid.Columns.Count == 0)
        {
            return;
        }

        dataGrid.SetValue(IsApplyingLayoutProperty, true);
        try
        {
            var saved = store.Get(profileId);
            var columns = dataGrid.Columns
                .Select(column => new
                {
                    Column = column,
                    FieldId = (string?)column.GetValue(FieldIdProperty),
                })
                .Where(static value => value.FieldId is not null)
                .Select(value => new
                {
                    value.Column,
                    State = saved.TryGetValue(value.FieldId!, out var state) ? state : null,
                })
                .Where(static value => value.State is not null)
                .OrderBy(static value => value.State!.DisplayIndex)
                .ToArray();

            foreach (var value in columns)
            {
                var state = value.State!;
                if (double.IsFinite(state.Width) && state.Width >= 24)
                {
                    value.Column.Width = new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
                }

                value.Column.DisplayIndex = Math.Clamp(
                    state.DisplayIndex + SelectionColumnOffset,
                    0,
                    dataGrid.Columns.Count - 1);
            }

            // The selector is UI chrome rather than profile data. Keep it at
            // the leading edge even when an older saved layout is restored.
            dataGrid.Columns[0].DisplayIndex = 0;
        }
        finally
        {
            dataGrid.SetValue(IsApplyingLayoutProperty, false);
        }
    }

    private static void OnColumnReordered(object? sender, DataGridColumnEventArgs eventArgs)
    {
        if (sender is DataGrid dataGrid)
        {
            SaveLayout(dataGrid);
        }
    }

    private static void OnGridMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is DataGrid dataGrid
            && eventArgs.OriginalSource is DependencyObject source
            && FindAncestor<Thumb>(source) is { } resizeThumb
            && FindAncestor<DataGridColumnHeader>(resizeThumb) is not null)
        {
            SaveLayout(dataGrid);
        }
    }

    private static void SaveLayout(DataGrid dataGrid)
    {
        if ((bool)dataGrid.GetValue(IsApplyingLayoutProperty))
        {
            return;
        }

        var profileId = GetProfileId(dataGrid);
        var store = GetLayoutStore(dataGrid);
        if (string.IsNullOrWhiteSpace(profileId) || store is null)
        {
            return;
        }

        var columns = dataGrid.Columns
            .Select(column => new
            {
                Column = column,
                FieldId = (string?)column.GetValue(FieldIdProperty),
            })
            .Where(static value => value.FieldId is not null)
            .Select(static value => new GridColumnLayout(
                value.FieldId!,
                Math.Max(0, value.Column.DisplayIndex - SelectionColumnOffset),
                value.Column.ActualWidth))
            .ToArray();
        store.Save(profileId, columns);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source switch
            {
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D =>
                    System.Windows.Media.VisualTreeHelper.GetParent(source),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        return null;
    }

    private sealed class SelectionCheckBoxColumn : DataGridColumn
    {
        private readonly string _selectionLabel;

        public SelectionCheckBoxColumn(IAppLocalizer? localizer)
        {
            _selectionLabel = L(
                localizer,
                "Loc.Catalog.Selection.Item",
                "항목 선택");
            Header = string.Empty;
            // This column stays frozen at the leading edge. The extra width is
            // intentional: WPF's focus chrome and the vertical grid line must
            // not crop the Fluent checkbox at the minimum window width.
            Width = new DataGridLength(52, DataGridLengthUnitType.Pixel);
            MinWidth = 52;
            MaxWidth = 52;
            CanUserReorder = false;
            CanUserResize = false;
            CanUserSort = false;
            IsReadOnly = false;
        }

        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            CreateCheckBox();

        protected override FrameworkElement GenerateEditingElement(
            DataGridCell cell,
            object dataItem) => CreateCheckBox();

        private CheckBox CreateCheckBox()
        {
            var checkBox = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _selectionLabel,
            };
            System.Windows.Automation.AutomationProperties.SetName(checkBox, _selectionLabel);
            BindingOperations.SetBinding(
                checkBox,
                ToggleButton.IsCheckedProperty,
                new Binding(nameof(DataGridRow.IsSelected))
                {
                    Mode = BindingMode.TwoWay,
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(DataGridRow),
                        1),
                });
            checkBox.PreviewMouseLeftButtonDown += OnCheckBoxPreviewMouseLeftButtonDown;
            return checkBox;
        }

        private static void OnCheckBoxPreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs eventArgs)
        {
            if (sender is not CheckBox checkBox
                || FindAncestor<DataGridRow>(checkBox) is not { } row)
            {
                return;
            }

            var shouldSelect = !row.IsSelected;

            // DataGridCell processes MouseLeftButtonDown even when a child marks
            // it handled. Give the cell a focused, selected row while that class
            // handler runs so a plain checkbox click cannot clear the existing
            // multi-selection. The final state is applied immediately after the
            // routed input event, which also neutralizes Ctrl's native re-toggle.
            _ = checkBox.Focus();
            if (shouldSelect)
            {
                row.IsSelected = true;
            }

            _ = checkBox.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => row.IsSelected = shouldSelect));
            eventArgs.Handled = true;
        }
    }

    private sealed class HighlightingTextColumn : DataGridTextColumn
    {
        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
        {
            var element = base.GenerateElement(cell, dataItem);
            if (element is not TextBlock textBlock)
            {
                return element;
            }

            var displayBinding = BindingOperations.GetBindingBase(textBlock, TextBlock.TextProperty);
            BindingOperations.ClearBinding(textBlock, TextBlock.TextProperty);
            if (displayBinding is not null)
            {
                BindingOperations.SetBinding(
                    textBlock,
                    TextHighlighting.DisplayTextProperty,
                    displayBinding);
            }

            BindingOperations.SetBinding(
                textBlock,
                TextHighlighting.HighlightTextProperty,
                new Binding("DataContext.FilterText")
                {
                    Mode = BindingMode.OneWay,
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(DataGrid),
                        1),
                });
            return textBlock;
        }
    }

    private sealed class MappedHighlightingTextColumn(string fieldId) : DataGridColumn
    {
        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            CreateContent();

        protected override FrameworkElement GenerateEditingElement(
            DataGridCell cell,
            object dataItem) => CreateContent();

        private FrameworkElement CreateContent()
        {
            var panel = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
            });

            var valueText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            BindingOperations.SetBinding(
                valueText,
                TextHighlighting.DisplayTextProperty,
                new Binding($"DisplayValues[{fieldId}]")
                {
                    Mode = BindingMode.OneWay,
                });
            BindingOperations.SetBinding(
                valueText,
                TextHighlighting.HighlightTextProperty,
                new Binding("DataContext.FilterText")
                {
                    Mode = BindingMode.OneWay,
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(DataGrid),
                        1),
                });
            panel.Children.Add(valueText);

            var mappingBadge = new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = "↔",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            mappingBadge.SetResourceReference(
                Border.BackgroundProperty,
                "ControlFillColorSecondaryBrush");
            mappingBadge.SetResourceReference(
                Border.BorderBrushProperty,
                "ControlStrokeColorDefaultBrush");
            mappingBadge.BorderThickness = new Thickness(1);
            BindingOperations.SetBinding(
                mappingBadge,
                UIElement.VisibilityProperty,
                new Binding($"MappedValueIndicators[{fieldId}]")
                {
                    Mode = BindingMode.OneWay,
                    Converter = new BooleanToVisibilityConverter(),
                });
            BindingOperations.SetBinding(
                mappingBadge,
                FrameworkElement.ToolTipProperty,
                new Binding($"MappedValueTooltips[{fieldId}]")
                {
                    Mode = BindingMode.OneWay,
                });
            Grid.SetColumn(mappingBadge, 1);
            panel.Children.Add(mappingBadge);
            return panel;
        }
    }
}
