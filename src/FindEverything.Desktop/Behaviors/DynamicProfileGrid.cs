using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FindEverything.Desktop.Configuration;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Behaviors;

public static class DynamicProfileGrid
{
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

    private static void OnFieldsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not DataGrid dataGrid)
        {
            return;
        }

        dataGrid.Columns.Clear();
        dataGrid.ColumnReordered -= OnColumnReordered;
        dataGrid.ColumnReordered += OnColumnReordered;
        dataGrid.PreviewMouseLeftButtonUp -= OnGridMouseLeftButtonUp;
        dataGrid.PreviewMouseLeftButtonUp += OnGridMouseLeftButtonUp;
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "상태",
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.CoverageText)),
            IsReadOnly = true,
            Width = DataGridLength.Auto,
        });

        if (eventArgs.NewValue is IEnumerable fields)
        {
            foreach (var field in fields.OfType<ProfileFieldDescriptor>().OrderBy(static field => field.Order))
            {
                var binding = new Binding($"[{field.FieldId}]")
                {
                    Mode = BindingMode.OneWay,
                    TargetNullValue = "—",
                };
                if (!string.IsNullOrWhiteSpace(field.DisplayFormat))
                {
                    binding.StringFormat = $"{{0:{field.DisplayFormat}}}";
                }

                var column = new DataGridTextColumn
                {
                    Header = field.Header,
                    Binding = binding,
                    IsReadOnly = true,
                    SortMemberPath = $"Values[{field.FieldId}]",
                    Width = DataGridLength.Auto,
                };
                column.SetValue(FieldIdProperty, field.FieldId);
                dataGrid.Columns.Add(column);
            }
        }

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "폴더 경로",
            Binding = new Binding(nameof(ViewModels.CatalogItemViewModel.FullPath)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });

        ApplyLayout(dataGrid);
    }

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
                    state.DisplayIndex,
                    0,
                    dataGrid.Columns.Count - 1);
            }
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
        if (sender is DataGrid dataGrid)
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
                value.Column.DisplayIndex,
                value.Column.ActualWidth))
            .ToArray();
        store.Save(profileId, columns);
    }
}
