using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs in the composition smoke test's existing application and STA.</summary>
internal static class FormSpacingUiChecks
{
    internal static void VerifyProfiles(Action<FrameworkElement, string>? capture = null)
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        var leaf = new string('W', 160) + "_2026";
        var whole = new GuidedPathChoiceViewModel(0, 3, leaf.Length, leaf, leaf, leaf, true);
        vm.GuidedPathSegments = [new(0, "\\", leaf, whole,
            [new(0, 3, 160, leaf[..160], leaf[..160], leaf[..160], false),
             new(0, 164, 4, "2026", "2026", "2026", false)])];
        vm.HasAnalyzedPath = true;
        vm.CurrentEditorStep = ProfileEditorStep.PathAndFields;
        var page = new ProfilesPage(vm);
        var host = new Ui.FluentWindow { Width = 760, Height = 760, Content = page };
        try
        {
            host.Show(); Pump(host);
            var list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            list.ScrollToSection("AssignmentPicker"); Pump(host);
            var button = Assert.IsType<Ui.Button>(page.FindEditorElement<Ui.Button>("GuidedWholePathChoiceButton"));
            var label = Assert.IsType<Ui.TextBlock>(button.Content);
            Assert.Same(vm.AssignPathChoiceCommand, button.Command);
            Assert.Same(whole, button.CommandParameter);
            Assert.Equal(leaf, button.ToolTip);
            Assert.Equal(leaf, label.Text);
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.InRange(button.ActualWidth, 1, 242);
            Assert.InRange(label.ActualWidth, 1, button.ActualWidth);
            AssertInsideWidth(button, page);
            capture?.Invoke(page, "Profiles-long-folder-choice-small.png");

            vm.AddTextFileFieldCommand.Execute(null);
            vm.CurrentEditorStep = ProfileEditorStep.DataSources; Pump(host);
            var field = Assert.Single(vm.DraftTextFileFields);
            list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            var entry = Assert.Single(list.Items.Cast<ProfileEditorEntry>(), item => ReferenceEquals(item.Value, field));
            list.ScrollIntoView(entry); Pump(host);
            var row = Assert.IsAssignableFrom<DependencyObject>(list.ItemContainerGenerator.ContainerFromItem(entry));
            var readLimit = Assert.Single(Descendants(row).OfType<Ui.TextBox>(), input =>
                BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path == nameof(field.MaxBytesText));
            var block = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(readLimit));
            var wrap = Assert.IsType<WrapPanel>(VisualTreeHelper.GetParent(block));
            var blocks = wrap.Children.OfType<StackPanel>().ToArray();
            Assert.Equal(4, blocks.Length);
            Assert.True(blocks[^1].TranslatePoint(new Point(), wrap).Y > blocks[0].TranslatePoint(new Point(), wrap).Y,
                "Narrow text-file forms must wrap instead of squeezing four editors into one line.");
            foreach (var child in blocks) AssertInsideWidth(child, wrap);

            var delete = Assert.Single(Descendants(row).OfType<Ui.Button>(), item => ReferenceEquals(item.Command, vm.RemoveTextFileFieldCommand));
            var headerGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(delete));
            var firstBlock = Assert.IsType<StackPanel>(headerGrid.Children[0]);
            var header = Assert.IsType<Ui.TextBlock>(firstBlock.Children[0]);
            var inputBox = Assert.IsType<Ui.TextBox>(firstBlock.Children[1]);
            header.Text = "A long descriptive field caption that wraps at larger text sizes";
            header.FontSize = 20;
            Pump(host);
            Assert.Equal(TextWrapping.Wrap, header.TextWrapping);
            var deleteBottom = delete.TranslatePoint(new Point(0, delete.ActualHeight), headerGrid).Y;
            var inputBottom = inputBox.TranslatePoint(new Point(0, inputBox.ActualHeight), headerGrid).Y;
            Assert.InRange(Math.Abs(deleteBottom - inputBottom), 0, 1);
        }
        finally { host.Close(); }
    }

    internal static void VerifyPlaygroundFooter(Window window, ProfilePlaygroundPage page)
    {
        var vm = Assert.IsType<ProfilePlaygroundViewModel>(page.DataContext);
        var originalSummary = vm.Summary;
        var originalWidth = window.Width;
        try
        {
            window.Width = 760;
            vm.Summary = string.Join(" ", Enumerable.Repeat("A long profile evaluation summary", 30));
            Pump(window);
            var panel = Assert.IsType<Grid>(page.FindName("PlaygroundSummaryPanel"));
            var text = Assert.IsType<Ui.TextBlock>(page.FindName("PlaygroundSummaryText"));
            Assert.True(panel.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
            Assert.Equal(vm.Summary, text.ToolTip);
            AssertInsideWidth(text, panel);
            AssertInsideWidth(panel, page);
        }
        finally { vm.Summary = originalSummary; window.Width = originalWidth; Pump(window); }
    }

    internal static void VerifyOutputHeading(Window window, OutputFormatsPage page)
    {
        var heading = Assert.IsType<WrapPanel>(page.FindName("OutputPreviewHeading"));
        var title = Assert.IsType<Ui.TextBlock>(page.FindName("OutputPreviewTitle"));
        var checkbox = Assert.IsType<CheckBox>(page.FindName("OutputUseTestSampleCheckBox"));
        var originalWidth = heading.Width;
        var originalVisibility = checkbox.Visibility;
        try
        {
            checkbox.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            Pump(window);
            // Both children fit individually, but not together on one line.
            heading.Width = Math.Max(title.DesiredSize.Width, checkbox.DesiredSize.Width) + 1;
            Pump(window);
            AssertInsideWidth(title, heading);
            AssertInsideWidth(checkbox, heading);
            Assert.True(checkbox.TranslatePoint(new Point(), heading).Y
                >= title.TranslatePoint(new Point(0, title.ActualHeight), heading).Y,
                "The sample toggle must wrap below the preview title when space is limited.");
        }
        finally
        {
            heading.Width = originalWidth;
            checkbox.SetCurrentValue(UIElement.VisibilityProperty, originalVisibility);
            Pump(window);
        }
    }

    internal static void VerifySettings(SettingsPage page) => Assert.Equal(TextWrapping.Wrap,
        Assert.IsType<Ui.TextBlock>(page.FindName("SettingsLanguageGuidance")).TextWrapping);

    private static void AssertInsideWidth(FrameworkElement child, FrameworkElement parent)
    {
        var left = child.TranslatePoint(new Point(), parent).X;
        Assert.True(child.ActualWidth > 0 && left >= -1 && left + child.ActualWidth <= parent.ActualWidth + 1,
            $"{child.GetType().Name} must fit its available width: left {left}, width {child.ActualWidth}, available {parent.ActualWidth}.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
