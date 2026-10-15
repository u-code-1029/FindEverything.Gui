using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Run in the shown navigation host after creating an editable output draft.</summary>
internal static class OutputFormatsViewportChecks
{
    internal static void Verify(Window window, OutputFormatsPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));
        Assert.True(Assert.IsType<OutputFormatsViewModel>(page.DataContext).CanEdit);
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var workspace = Assert.IsType<SplitWorkspace>(page.FindName("OutputWorkspace"));
        var originalTab = Descendants(workspace).OfType<TabControl>().SingleOrDefault()?.SelectedIndex;
        var scroll = Assert.IsAssignableFrom<ScrollViewer>(page.FindName("OutputFormatsScrollViewer"));
        var originalOffset = scroll.VerticalOffset;
        try
        {
            foreach (var size in new[] { new Size(1360, 860), new Size(760, 640) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                Pump(window);
                var tabs = Descendants(workspace).OfType<TabControl>().SingleOrDefault();
                if (tabs is not null)
                {
                    tabs.SelectedIndex = 0;
                    Pump(window);
                    var first = Assert.IsAssignableFrom<TabItem>(tabs.Items[0]);
                    var second = Assert.IsAssignableFrom<TabItem>(tabs.Items[1]);
                    Assert.True(first.Focusable && second.Focusable);
                    Assert.True(first.Focus());
                    Assert.Equal(0, tabs.SelectedIndex);
                }

                // Comparing only with page.ActualHeight misses a page expanded
                // beyond the HWND by NavigationView's outer scroll presenter.
                var viewport = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertInside(page, viewport, "The output page must be bounded by the window viewport.");
                var footer = Assert.IsType<CommandStrip>(page.FindName("OutputActionsFooter"));
                var save = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("SaveFormatButton"));
                AssertInside(footer, viewport, "The output action footer must stay on screen.");
                AssertInside(save, viewport, "Save must remain reachable without outer-page scrolling.");
                Assert.True(save.IsEnabled);
                Assert.True(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0,
                    "The output editor must own a bounded, vertically scrollable viewport.");
                var footerTop = footer.TranslatePoint(new Point(), viewport).Y;
                Assert.True(scroll.TranslatePoint(new Point(0, scroll.ActualHeight), viewport).Y <= footerTop + 1,
                    "The editor must not overlap the persistent footer.");

                foreach (var name in new[] { "TemplateTextBox", "OutputPrefixTextBox", "OutputSuffixTextBox" })
                {
                    var input = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName(name));
                    input.BringIntoView();
                    Pump(window);
                    Assert.True(input.Focus());
                    Pump(window);
                    AssertInside(input, viewport, $"{name} must be reachable by scrolling the editor.");
                    Assert.True(input.TranslatePoint(new Point(0, input.ActualHeight), viewport).Y <= footerTop + 1);
                }
                Assert.True(scroll.VerticalOffset > 0);
            }
        }
        finally
        {
            window.Width = originalWidth;
            window.Height = originalHeight;
            Pump(window);
            if (originalTab is { } selected && Descendants(workspace).OfType<TabControl>().SingleOrDefault() is { } tabs)
            {
                tabs.SelectedIndex = selected;
                Pump(window);
            }
            scroll.ScrollToVerticalOffset(originalOffset);
            Pump(window);
        }
    }

    private static void AssertInside(FrameworkElement element, FrameworkElement viewport, string message)
    {
        var origin = element.TranslatePoint(new Point(), viewport);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, message);
        Assert.True(origin.X >= -1 && origin.Y >= -1
            && origin.X + element.ActualWidth <= viewport.ActualWidth + 1
            && origin.Y + element.ActualHeight <= viewport.ActualHeight + 1, message);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
