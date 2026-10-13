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
            var panel = Assert.IsType<WorkspaceTransferPanel>(page.FindName(name));
            Assert.Same(transfer, panel.DataContext);
            Assert.True(panel.AllowDrop);
            Assert.Equal(profileOnly, panel.ShowProfileExport);
            Assert.Equal(!profileOnly, panel.ShowPackageExport);
            Assert.Equal(!profileOnly, panel.ShowSourceSelector);
            if (!profileOnly) Assert.Same(page.DataContext, panel.ExportOwner);
            Assert.True(HasAncestor<PageHeader>(panel), "Sharing must fit in the existing header, not add a results toolbar row.");
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
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
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
