using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Run on the composition application's dispatcher with a visible results grid.</summary>
internal static class CatalogHeaderSelectionUiChecks
{
    internal static void Verify(DataGrid grid, CatalogViewModel owner, Action flush)
    {
        flush();
        var header = Assert.IsType<CheckBox>(grid.Columns[0].Header);
        Assert.Equal("CatalogSelectAllCheckBox", header.Name);
        Assert.False(header.IsThreeState);
        Assert.Same(owner.ToggleCurrentResultsSelectionCommand, header.Command);
        Assert.True(header.IsVisible);
        Assert.True(header.IsEnabled);
        Assert.True(header.IsHitTestVisible);
        Assert.True(header.ActualWidth > 0 && header.ActualHeight > 0);
        var center = header.TranslatePoint(new Point(header.ActualWidth / 2, header.ActualHeight / 2), grid);
        var hit = grid.InputHitTest(center) as DependencyObject;
        Assert.Same(header, FindAncestor<CheckBox>(hit));

        ClickAndVerify(selected: true);
        ClickAndVerify(selected: false);
        ClickAndVerify(selected: true);

        void ClickAndVerify(bool selected)
        {
            // This is the control's complete native click path, not just a
            // direct command invocation: OnToggle, routed Click, then Command.
            typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(header, null);
            var timeout = Stopwatch.StartNew();
            while ((owner.IsPreservingSelection || owner.CurrentResultsSelectionState != selected
                    || FindVisualChildren<DataGridRow>(grid).Any(row =>
                        row.Item is CatalogItemViewModel { IsPlaceholder: false } item && item.IsSelected != selected))
                   && timeout.Elapsed < TimeSpan.FromSeconds(8)) flush();
            flush();
            Assert.False(owner.IsPreservingSelection);
            Assert.Equal(selected, owner.CurrentResultsSelectionState);
            Assert.Equal(selected, header.IsChecked);
            Assert.True(BindingOperations.IsDataBound(header, ToggleButton.IsCheckedProperty));

            var visibleRows = FindVisualChildren<DataGridRow>(grid)
                .Where(row => row.Item is CatalogItemViewModel { IsPlaceholder: false }).ToArray();
            Assert.NotEmpty(visibleRows);
            foreach (var row in visibleRows)
            {
                Assert.Equal(selected, ((CatalogItemViewModel)row.Item).IsSelected);
                var selector = Assert.Single(FindVisualChildren<CheckBox>(row));
                Assert.Equal(selected, selector.IsChecked);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result) return result;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
