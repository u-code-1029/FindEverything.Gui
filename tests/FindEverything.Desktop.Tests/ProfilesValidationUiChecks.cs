using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Uses the composition smoke test's existing application and STA.</summary>
internal static class ProfilesValidationUiChecks
{
    public static void Verify(Action<FrameworkElement, string>? capture = null)
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        var page = new ProfilesPage(vm);
        var host = new Ui.FluentWindow { Width = 1050, Height = 720, Content = page };
        try
        {
            host.Show(); Pump(); host.UpdateLayout();
            Assert.False(vm.HasValidationIssues);
            vm.CurrentEditorStep = ProfileEditorStep.Review;
            vm.DraftMaxScanDepthText = "invalid";
            Pump(); host.UpdateLayout();
            page.ActiveEditorList!.ScrollToSection("ValidationIssues");
            Pump(); host.UpdateLayout(); Pump();
            var card = Assert.IsAssignableFrom<Ui.CardControl>(page.FindEditorElement<FrameworkElement>("ProfileValidationIssuesCard"));
            Assert.True(card.IsVisible);
            var issue = Assert.Single(vm.ValidationIssues);
            var button = Assert.Single(Descendants(card).OfType<Ui.Button>());
            Assert.Equal(issue.NavigationText, button.Content);
            Assert.Same(vm.GoToEditorStepCommand, button.Command);
            Assert.Equal(ProfileEditorStep.ScanOptions, button.CommandParameter);
            Assert.True(button.IsEnabled);
            Assert.True(button.ActualWidth > 80);
            var text = Assert.Single(Descendants(card).OfType<Ui.TextBlock>(), block => block.Text == issue.Message);
            Assert.Same(page.FindResource("SystemFillColorCriticalBrush"), text.Foreground);
            Assert.True(text.ActualWidth > 300);
            capture?.Invoke(page, "Profiles-validation-step-links.png");
            var peer = new ButtonAutomationPeer(button);
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Pump();
            Assert.Equal(ProfileEditorStep.ScanOptions, vm.CurrentEditorStep);
            vm.DraftMaxScanDepthText = "256";
            Pump();
            Assert.False(vm.HasValidationIssues);

            // Validation shares the editor's real scroll surface: many errors
            // cannot consume an Auto row and starve all editable content.
            host.Height = 640;
            vm.ValidationIssues = Enumerable.Range(0, 12).Select(index => new ProfileValidationIssueViewModel(
                "example-" + index, "A validation error that must remain reachable " + index,
                ProfileEditorStep.ScanOptions, "Scan options")).ToArray();
            Pump(); host.UpdateLayout(); Pump();
            Assert.True(page.EditorScrollViewer!.ActualHeight > 100);
            var list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            var last = list.Items.Cast<ProfileEditorEntry>().Last();
            list.ScrollIntoView(last);
            Pump(); host.UpdateLayout(); Pump();
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromItem(last));
            Assert.True(page.EditorScrollViewer.VerticalOffset > 0);
        }
        finally { host.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
