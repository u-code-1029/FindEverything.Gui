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

public static partial class DynamicProfileGrid
{
    private const int SelectionColumnOffset = 1;
    private static readonly DependencyProperty ActiveEditorOwnerProperty = DependencyProperty.RegisterAttached(
        "ActiveEditorOwner", typeof(ViewModels.CatalogViewModel), typeof(DynamicProfileGrid));

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
        InitializeSizing(dataGrid);
        var fieldsSnapshot = (GetFields(dataGrid) as IEnumerable)?.OfType<ProfileFieldDescriptor>()
            .OrderBy(static field => field.Order).ToArray() ?? [];
        var signature = GetSchemaSignature(dataGrid, fieldsSnapshot);
        var sizing = GetSizing(dataGrid);
        if (sizing.Schema == signature && dataGrid.Columns.Count > 0) return;
        sizing.Schema = signature;
        sizing.MeasuredWidths.Clear();
        sizing.HasMeasured = false;
        dataGrid.FrozenColumnCount = 0;
        dataGrid.Columns.Clear();
        dataGrid.ColumnReordered -= OnColumnReordered;
        dataGrid.ColumnReordered += OnColumnReordered;
        dataGrid.PreviewMouseLeftButtonUp -= OnGridMouseLeftButtonUp;
        dataGrid.PreviewMouseLeftButtonUp += OnGridMouseLeftButtonUp;
        dataGrid.Columns.Add(new SelectionCheckBoxColumn(localizer));
        dataGrid.FrozenColumnCount = SelectionColumnOffset;
        var statusColumn = new HighlightingTextColumn
        {
            Header = L(localizer, "Loc.Catalog.Column.Status", "상태"),
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.CoverageText)),
            IsReadOnly = true,
            Width = DataGridLength.Auto,
            MinWidth = 80,
            MaxWidth = 160,
        };
        statusColumn.SetValue(FieldIdProperty, StatusColumnId);
        dataGrid.Columns.Add(statusColumn);

        if (GetFields(dataGrid) is IEnumerable fields)
        {
            foreach (var field in fieldsSnapshot)
            {
                if (field.SourceKind == ProfileFieldSourceKind.UserInput)
                {
                    var inputColumn = new UserInputTextColumn(field)
                    {
                        Header = field.Header,
                        IsReadOnly = false,
                        SortMemberPath = $"UserInputValues[{field.FieldId}].Value",
                        Width = new DataGridLength(180, DataGridLengthUnitType.Pixel),
                        MinWidth = 120,
                        MaxWidth = 560,
                    };
                    inputColumn.SetValue(FieldIdProperty, field.FieldId);
                    dataGrid.Columns.Add(inputColumn);
                    continue;
                }

                var column = new MappedHighlightingTextColumn(field.FieldId)
                {
                    Header = CreateFieldHeader(field, localizer),
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

        var pathColumn = new HighlightingTextColumn
        {
            Header = L(localizer, "Loc.Catalog.Column.FolderPath", "폴더 경로"),
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.FullPath)),
            IsReadOnly = true,
            Width = new DataGridLength(240),
            MinWidth = 180,
            MaxWidth = 720,
        };
        pathColumn.SetValue(FieldIdProperty, PathColumnId);
        dataGrid.Columns.Add(pathColumn);

        ApplyLayout(dataGrid);
        ScheduleSizing(dataGrid);
    }

    private static object CreateFieldHeader(ProfileFieldDescriptor field, IAppLocalizer? localizer)
    {
        if (field.SourceKind != ProfileFieldSourceKind.TextFileContent) return field.Header;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = field.Header, VerticalAlignment = VerticalAlignment.Center });
        var label = L(localizer, "Loc.Catalog.Column.Refresh", "이 컬럼 다시 읽기");
        var button = new Wpf.Ui.Controls.Button
        {
            Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowSync20),
            CommandParameter = field.FieldId,
            ToolTip = L(localizer, "Loc.Catalog.Column.Refresh.Help", "필터·선택과 관계없이 현재 프로필 결과 전체에서 이 컬럼의 텍스트 파일만 다시 읽습니다. 다른 컬럼과 폴더 탐색은 변경하지 않습니다."),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        button.SetBinding(Button.CommandProperty, new Binding("DataContext.RefreshTextColumnCommand")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
        });
        button.SetBinding(UIElement.IsEnabledProperty, new Binding("DataContext.CanEditWorkspace")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
        });
        panel.Children.Add(button);
        return panel;
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
            InitializeSizing(dataGrid);
            ApplyLayout(dataGrid);
            if (GetSizing(dataGrid).ProfileId != GetProfileId(dataGrid)) _ = LoadPresetsAsync(dataGrid);
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
            foreach (var column in dataGrid.Columns.Skip(1)) column.SetValue(IsManualWidthProperty, false);
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
                value.Column.SetValue(IsManualWidthProperty, state.WidthMode == GridColumnWidthMode.Manual);
                if (state.WidthMode == GridColumnWidthMode.Manual && double.IsFinite(state.Width) && state.Width >= 24)
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
        ScheduleSizing(dataGrid);
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
            if (FindAncestor<DataGridColumnHeader>(resizeThumb)?.Column is { CanUserResize: true } column)
                column.SetValue(IsManualWidthProperty, true);
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
                value.Column.ActualWidth,
                (bool)value.Column.GetValue(IsManualWidthProperty) ? GridColumnWidthMode.Manual : GridColumnWidthMode.Auto))
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
            var invertButton = new Wpf.Ui.Controls.Button
            {
                Width = 32, Height = 28, Padding = new Thickness(0),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowSwap20),
                ToolTip = L(localizer, "Loc.Selection.Invert.ToolTip", "현재 검색 결과 전체에서 선택을 반전합니다. 미로딩 항목도 포함됩니다."),
            };
            System.Windows.Automation.AutomationProperties.SetName(invertButton,
                L(localizer, "Loc.Workspace.Selection.Invert", "선택 반전"));
            invertButton.SetBinding(Button.CommandProperty, new Binding("DataContext.InvertSelectionCommand")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
            });
            Header = invertButton;
            // This column stays frozen at the leading edge. The extra width is
            // intentional: WPF's focus chrome and the vertical grid line must
            // not crop the Fluent checkbox at the minimum window width.
            Width = new DataGridLength(64, DataGridLengthUnitType.Pixel);
            MinWidth = 64;
            MaxWidth = 64;
            var cellStyle = new Style(typeof(DataGridCell),
                System.Windows.Application.Current?.TryFindResource("DefaultDataGridCellStyle") as Style
                ?? System.Windows.Application.Current?.TryFindResource(typeof(DataGridCell)) as Style);
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
            cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            CellStyle = cellStyle;
            CanUserReorder = false;
            CanUserResize = false;
            CanUserSort = false;
            IsReadOnly = false;
        }

        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            CreateCheckBox(cell);

        protected override FrameworkElement GenerateEditingElement(
            DataGridCell cell,
            object dataItem) => CreateCheckBox(cell);

        private CheckBox CreateCheckBox(DataGridCell cell)
        {
            var checkBox = new CheckBox
            {
                Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
                Padding = new Thickness(0), Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _selectionLabel,
            };
            System.Windows.Automation.AutomationProperties.SetName(checkBox, _selectionLabel);
            var hasCatalogSelection = FindAncestor<DataGrid>(cell)?.DataContext is ViewModels.CatalogViewModel;
            BindingOperations.SetBinding(
                checkBox,
                ToggleButton.IsCheckedProperty,
                hasCatalogSelection
                    ? new Binding(nameof(ViewModels.CatalogItemViewModel.IsSelected)) { Mode = BindingMode.OneWay }
                    : new Binding(nameof(DataGridRow.IsSelected))
                    {
                        Mode = BindingMode.TwoWay,
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                    });
            checkBox.PreviewMouseLeftButtonDown += OnCheckBoxPreviewMouseLeftButtonDown;
            checkBox.PreviewKeyDown += OnCheckBoxPreviewKeyDown;
            checkBox.Checked += OnCheckBoxStateChanged;
            checkBox.Unchecked += OnCheckBoxStateChanged;
            return checkBox;
        }

        private static async void OnCheckBoxStateChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox
                || FindAncestor<DataGrid>(checkBox)?.DataContext is not ViewModels.CatalogViewModel viewModel
                || checkBox.DataContext is not ViewModels.CatalogItemViewModel { IsPlaceholder: false } item
                || checkBox.IsChecked == item.IsSelected) return;
            // UI Automation invokes TogglePattern without a pointer event. A
            // model-driven binding update already has the desired state.
            await viewModel.SelectRowAsync(item, additive: true, range: false);
        }

        private static async void OnCheckBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || sender is not CheckBox checkBox
                || FindAncestor<DataGrid>(checkBox)?.DataContext is not ViewModels.CatalogViewModel viewModel
                || FindAncestor<DataGridRow>(checkBox)?.Item is not ViewModels.CatalogItemViewModel { IsPlaceholder: false } item) return;
            e.Handled = true;
            await viewModel.SelectRowAsync(item, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        private static async void OnCheckBoxPreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs eventArgs)
        {
            if (sender is not CheckBox checkBox
                || FindAncestor<DataGridRow>(checkBox) is not { } row)
            {
                return;
            }

            if (FindAncestor<DataGrid>(checkBox)?.DataContext is ViewModels.CatalogViewModel viewModel)
            {
                eventArgs.Handled = true;
                if (row.Item is not ViewModels.CatalogItemViewModel { IsPlaceholder: false } item) return;
                checkBox.Focus();
                await viewModel.SelectRowAsync(item, additive: true, range: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
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
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            if (Binding is not null) BindingOperations.SetBinding(textBlock, FrameworkElement.ToolTipProperty, Binding);

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
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var valueText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
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
            valueText.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"DisplayValues[{fieldId}]") { Mode = BindingMode.OneWay });

            var mappingBadge = new Views.Controls.DisplayBadge
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                Content = new TextBlock
                {
                    Text = "↔",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            mappingBadge.SetResourceReference(
                Control.BackgroundProperty,
                "ControlFillColorSecondaryBrush");
            mappingBadge.SetResourceReference(
                Control.BorderBrushProperty,
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
            var errorBadge = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning20)
            {
                Width = 16, Height = 16, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            errorBadge.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCautionBrush");
            errorBadge.SetBinding(UIElement.VisibilityProperty, new Binding("FieldErrors")
            {
                Converter = new FieldErrorConverter(visibility: true), ConverterParameter = fieldId,
            });
            errorBadge.SetBinding(FrameworkElement.ToolTipProperty, new Binding("FieldErrors")
            {
                Converter = new FieldErrorConverter(visibility: false), ConverterParameter = fieldId,
            });
            BindingOperations.SetBinding(errorBadge, System.Windows.Automation.AutomationProperties.NameProperty, new Binding("FieldErrors")
            {
                Converter = new FieldErrorConverter(visibility: false), ConverterParameter = fieldId,
            });
            Grid.SetColumn(errorBadge, 2);
            panel.Children.Add(errorBadge);
            var previewBadge = new Views.Controls.DisplayBadge
            {
                Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = "…", FontWeight = FontWeights.SemiBold },
            };
            previewBadge.SetResourceReference(Control.BackgroundProperty, "ControlFillColorSecondaryBrush");
            previewBadge.SetBinding(UIElement.VisibilityProperty, new Binding("PreviewTooltips")
            {
                Converter = new FieldErrorConverter(visibility: true), ConverterParameter = fieldId,
            });
            previewBadge.SetBinding(FrameworkElement.ToolTipProperty, new Binding("PreviewTooltips")
            {
                Converter = new FieldErrorConverter(visibility: false), ConverterParameter = fieldId,
            });
            BindingOperations.SetBinding(previewBadge, System.Windows.Automation.AutomationProperties.NameProperty, new Binding("PreviewTooltips")
            {
                Converter = new FieldErrorConverter(visibility: false), ConverterParameter = fieldId,
            });
            Grid.SetColumn(previewBadge, 3);
            panel.Children.Add(previewBadge);
            return panel;
        }
    }

    private sealed class FieldErrorConverter(bool visibility) : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var error = value is IReadOnlyDictionary<string, string> errors && parameter is string fieldId
                && errors.TryGetValue(fieldId, out var message) ? message : null;
            return visibility ? string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible : error;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class UserInputTextColumn(ProfileFieldDescriptor field) : DataGridColumn
    {
        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            CreateTextBox();

        protected override FrameworkElement GenerateEditingElement(
            DataGridCell cell,
            object dataItem) => CreateTextBox();

        private FrameworkElement CreateTextBox()
        {
            var textBox = new Wpf.Ui.Controls.TextBox
            {
                Margin = new Thickness(4, 2, 4, 2),
                MinWidth = 96,
                MaxLength = ProfileManifestLimits.MaximumUserInputValueLength,
                ClearButtonEnabled = true,
                PlaceholderText = field.Placeholder ?? string.Empty,
                ToolTip = field.Placeholder,
            };
            BindingOperations.SetBinding(
                textBox,
                System.Windows.Controls.TextBox.TextProperty,
                new Binding($"UserInputValues[{field.FieldId}].Value")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            textBox.LostKeyboardFocus += OnLostKeyboardFocus;
            textBox.GotKeyboardFocus += OnGotKeyboardFocus;
            textBox.Unloaded += (_, _) => EndEditor(textBox);
            return textBox;
        }

        private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
        {
            if (sender is not Wpf.Ui.Controls.TextBox textBox
                || textBox.GetValue(ActiveEditorOwnerProperty) is not null
                || FindAncestor<DataGrid>(textBox)?.DataContext is not ViewModels.CatalogViewModel viewModel) return;
            viewModel.BeginUserInputEdit();
            textBox.SetValue(ActiveEditorOwnerProperty, viewModel);
        }

        private static void EndEditor(Wpf.Ui.Controls.TextBox textBox)
        {
            if (textBox.GetValue(ActiveEditorOwnerProperty) is not ViewModels.CatalogViewModel owner) return;
            textBox.ClearValue(ActiveEditorOwnerProperty);
            owner.EndUserInputEdit();
        }

        private async void OnLostKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs eventArgs)
        {
            if (sender is not Wpf.Ui.Controls.TextBox textBox || textBox.IsKeyboardFocusWithin) return;
            try
            {
                if (textBox.DataContext is not ViewModels.CatalogItemViewModel item) return;
                var binding = BindingOperations.GetBindingExpression(
                    textBox,
                    System.Windows.Controls.TextBox.TextProperty);
                binding?.UpdateSource();
                if (item.UserInputValues.TryGetValue(field.FieldId, out var input))
                {
                    try
                    {
                        await input.CommitAsync().ConfigureAwait(true);
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or NotSupportedException
                            or System.Data.Common.DbException)
                    {
                        // CatalogViewModel already logged and surfaced the save
                        // failure. Keep the value dirty so the next focus loss or
                        // shutdown flush can retry it without crashing the UI.
                    }
                }
            }
            finally
            {
                // Refocusing the same editor while its save awaited must keep it
                // protected. Unloading a recycled row also releases the owner.
                if (!textBox.IsKeyboardFocusWithin) EndEditor(textBox);
            }
        }
    }
}
