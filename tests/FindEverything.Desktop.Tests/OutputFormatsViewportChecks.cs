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
        Assert.True(workspace.KeepSecondaryVisible);
        var scroll = Assert.IsAssignableFrom<ScrollViewer>(page.FindName("OutputFormatsScrollViewer"));
        var originalOffset = scroll.VerticalOffset;
        var affixes = Assert.IsAssignableFrom<Expander>(page.FindName("OutputAffixesExpander"));
        var originalExpansion = affixes.IsExpanded;
        try
        {
            affixes.IsExpanded = true;
            foreach (var size in new[] { new Size(1360, 860), new Size(760, 640) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                Pump(window);
                Assert.Empty(Descendants(workspace).OfType<TabControl>());

                // Comparing only with page.ActualHeight misses a page expanded
                // beyond the HWND by NavigationView's outer scroll presenter.
                var viewport = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertInside(page, viewport, "The output page must be bounded by the window viewport.");
                var preview = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("OutputPreviewTextBox"));
                Assert.True(preview.IsVisible && preview.IsReadOnly);
                AssertInside(preview, viewport, "The result preview must stay visible while editing, even in a narrow window.");
                Assert.True(preview.ActualHeight >= 24, "The compact preview must retain at least one readable line.");
                if (workspace.ActualWidth < 900)
                {
                    var previewCard = Assert.IsType<SectionCard>(page.FindName("OutputPreviewCard"));
                    Assert.Equal(3, workspace.RowDefinitions.Count);
                    Assert.Equal(2, Grid.GetRow(previewCard));
                    Assert.True(scroll.TranslatePoint(new Point(0, scroll.ActualHeight), viewport).Y
                        <= previewCard.TranslatePoint(new Point(), viewport).Y - 7,
                        "The editor and persistent preview need their own non-overlapping stacked work areas.");
                }
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
            affixes.IsExpanded = originalExpansion;
            window.Width = originalWidth;
            window.Height = originalHeight;
            Pump(window);
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
