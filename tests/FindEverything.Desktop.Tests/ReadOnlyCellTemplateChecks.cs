using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.Views.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the existing composition application/STA dispatcher.</summary>
internal static class ReadOnlyCellTemplateChecks
{
    internal static void Verify()
    {
        var value = new CellValue("A long read-only path " + new string('x', 256));
        var cell = new SelectableCatalogText(new Binding(nameof(CellValue.Value)) { Mode = BindingMode.OneWay })
        {
            DataContext = value, Width = 180, VerticalAlignment = VerticalAlignment.Center,
        };
        var other = new Wpf.Ui.Controls.Button { Content = "Focus elsewhere", Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(cell); panel.Children.Add(other);
        var window = new Window { Width = 300, Height = 180, Content = panel, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var editor = cell.TextBox;
            Assert.IsType<Wpf.Ui.Controls.TextBox>(editor);
            Assert.True(editor.IsReadOnly);
            Assert.True(editor.OverridesDefaultStyle);
            Assert.Null(editor.Style.BasedOn);
            Assert.False(editor.PlaceholderEnabled);
            editor.ApplyTemplate();
            var host = Assert.IsType<ScrollViewer>(editor.Template.FindName("PART_ContentHost", editor));
            host.ApplyTemplate();
            Assert.IsType<ScrollContentPresenter>(host.Template.FindName("PART_ScrollContentPresenter", host));
            Assert.Empty(Descendants<ButtonBase>(editor));
            Assert.Empty(Descendants<Border>(editor));
            Assert.Empty(Descendants<Grid>(editor));
            Assert.Empty(Descendants<ScrollBar>(editor));
            editor.Focus();
            editor.Select(0, 6);
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.Equal("A long", editor.SelectedText);
            Assert.Equal(1, editor.Opacity);
            Assert.Equal(Visibility.Collapsed, cell.Highlight.Visibility);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, editor));
            Assert.False(ApplicationCommands.Cut.CanExecute(null, editor));
            editor.ScrollToHorizontalOffset(100);
            editor.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            editor.UpdateLayout();
            Assert.True(host.ScrollableWidth > 0);
            Assert.True(host.HorizontalOffset > 0, "A minimal text host must retain native horizontal selection scrolling.");
            other.Focus();
            Assert.Equal(0, editor.Opacity);
            Assert.Equal(Visibility.Visible, cell.Highlight.Visibility);
            Assert.Equal(value.Value, editor.Text);
        }
        finally { window.Close(); }
    }

    private sealed record CellValue(string Value);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
