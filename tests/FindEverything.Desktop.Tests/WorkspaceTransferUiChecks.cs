using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Uses the composition smoke test's single application/STA dispatcher.</summary>
internal static class WorkspaceTransferUiChecks
{
    public static void Verify(IServiceProvider services, MainWindow window, Action<FrameworkElement, string>? capture = null)
    {
        var transfer = services.GetRequiredService<WorkspaceTransferViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        foreach (var (page, name, profileOnly) in new (Page Page, string Name, bool ProfileOnly)[]
        {
            (services.GetRequiredService<CatalogPage>(), "CatalogWorkspaceTransferPanel", false),
            (services.GetRequiredService<FilesPage>(), "FilesWorkspaceTransferPanel", false),
            (services.GetRequiredService<ProfilesPage>(), "ProfilesWorkspaceTransferPanel", true),
        })
        {
            Assert.True(navigation.Navigate(page.GetType()));
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            WaitForPageArrangement(window, page);
            var panel = Assert.IsType<WorkspaceTransferPanel>(page.FindName(name));
            Assert.Same(transfer, panel.DataContext);
            Assert.True(panel.AllowDrop);
            Assert.Equal(profileOnly, panel.ShowProfileExport);
            Assert.Equal(!profileOnly, panel.ShowPackageExport);
            Assert.Equal(!profileOnly, panel.ShowSourceSelector);
            if (!profileOnly) Assert.Same(page.DataContext, panel.ExportOwner);
            Assert.False(HasAncestor<PageHeader>(panel), "Page titles must not be reintroduced just to host sharing.");
            Assert.True(profileOnly ? HasAncestor<Ui.TabViewItem>(panel) : HasAncestor<CommandStrip>(panel),
                "Sharing must remain in an existing tab strip or location controls, without a new page header row.");
            Assert.InRange(panel.ActualHeight, 1, 36);
            var button = Assert.IsType<Ui.Button>(panel.FindName("TransferMenuButton"));
            Assert.NotNull(button.ToolTip);
            Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)));
            var flyout = Assert.IsType<Ui.Flyout>(panel.FindName("TransferFlyout"));
            var picker = Assert.IsType<ComboBox>(panel.FindName("SourcePicker"));
            Assert.Same(transfer.Sources, picker.ItemsSource);
            var size = page.RenderSize;
            try
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Input);
                Assert.True(flyout.IsOpen);
                var popup = Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout));
                Assert.Same(button, popup.PlacementTarget);
                Assert.False(popup.StaysOpen);
                var popupContent = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                WaitForPopupArrangement(window.Dispatcher, popup, popupContent);
                Assert.True(popupContent.IsVisible);
                Assert.True(popupContent.ActualWidth > 300);
                Assert.True(popupContent.ActualHeight > 0);
                capture?.Invoke(popupContent, $"{page.GetType().Name}-source-sharing-popup.png");
                Assert.Equal(size, page.RenderSize);
                Assert.IsType<Ui.DynamicScrollViewer>(flyout.Content);
                Assert.True(picker.IsEnabled);
                if (profileOnly) Assert.False(picker.IsVisible);
                else Assert.True(picker.IsVisible);
            }
            finally { flyout.Hide(); }
        }
    }

    private static void WaitForPageArrangement(MainWindow window, Page page)
    {
        // Earlier QA cases resize the HWND and manually arrange cached pages.
        // Capture the real, connected viewport only after those native size and
        // navigation/load messages settle, not the last QA fixture's RenderSize.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var earliest = DateTime.UtcNow + TimeSpan.FromMilliseconds(80);
        Size previous = default;
        var stablePasses = 0;
        bool Ready()
        {
            if (!window.IsLoaded || !page.IsLoaded || PresentationSource.FromVisual(page) is null
                || !page.IsMeasureValid || !page.IsArrangeValid || page.ActualWidth <= 0 || page.ActualHeight <= 0)
                return false;
            var origin = page.TranslatePoint(new Point(), window);
            return origin.X >= -1 && origin.Y >= -1
                && origin.X + page.ActualWidth <= window.ActualWidth + 1
                && origin.Y + page.ActualHeight <= window.ActualHeight + 1;
        }
        while (DateTime.UtcNow < deadline)
        {
            PumpNativeLayout(window.Dispatcher);
            window.UpdateLayout();
            page.UpdateLayout();
            if (Ready() && page.RenderSize == previous) stablePasses++;
            else stablePasses = 0;
            previous = page.RenderSize;
            if (stablePasses >= 2 && DateTime.UtcNow >= earliest) return;
        }
        Assert.Fail($"Page must settle before opening sharing: loaded={page.IsLoaded}, connected={PresentationSource.FromVisual(page) is not null}, "
            + $"page={page.RenderSize}, window={window.ActualWidth:0.##}×{window.ActualHeight:0.##}, requested={window.Width:0.##}×{window.Height:0.##}.");
    }

    private static void PumpNativeLayout(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void WaitForPopupArrangement(Dispatcher dispatcher, Popup popup, FrameworkElement content)
    {
        // Popup creates a separate HWND. A synchronous UpdateLayout on its
        // still-detached child (or one Input-priority no-op) cannot arrange it;
        // let the actual HWND/load/render queue settle before checking bounds.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        bool Ready() => popup.IsOpen && content.IsMeasureValid && content.IsArrangeValid
            && content.ActualWidth > 0 && content.ActualHeight > 0;
        while (!Ready() && DateTime.UtcNow < deadline)
        {
            PumpNativeLayout(dispatcher);
            content.UpdateLayout();
        }
        Assert.True(Ready(), $"Shared-data popup must actually arrange: open={popup.IsOpen}, "
            + $"visible={content.IsVisible}, measure={content.IsMeasureValid}, arrange={content.IsArrangeValid}, "
            + $"size={content.ActualWidth:0.##}×{content.ActualHeight:0.##}.");
    }

    private static bool HasAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T) return true;
            element = LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}
