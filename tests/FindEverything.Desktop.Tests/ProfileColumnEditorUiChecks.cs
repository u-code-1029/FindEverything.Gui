using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Runtime;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs inside the existing composition smoke test's application and STA.</summary>
internal static class ProfileColumnEditorUiChecks
{
    public static void Verify(Action<FrameworkElement, string>? capture = null)
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        vm.EditorMode = ProfileEditorMode.Expert;
        vm.CurrentEditorStep = ProfileEditorStep.PathAndFields;
        var page = new ProfilesPage(vm);
        var host = new Ui.FluentWindow { Width = 1050, Height = 800, Content = page };
        try
        {
            host.Show(); Pump(); host.UpdateLayout(); Pump();
            var list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            var entries = list.Items.Cast<ProfileEditorEntry>().ToArray();
            var columnsIndex = Array.FindIndex(entries, entry => entry.Section.Id == "ExpertColumns");
            var rulesIndex = Array.FindIndex(entries, entry => entry.Section.Id == "ExpertRules");
            var field = Assert.Single(vm.DraftFields);
            var fieldIndex = Array.FindIndex(entries, entry => ReferenceEquals(entry.Value, field));
            Assert.True(columnsIndex >= 0 && columnsIndex < fieldIndex && fieldIndex < rulesIndex);
            var add = Assert.IsType<Ui.Button>(page.FindEditorElement<Ui.Button>("ExpertAddColumnButton"));
            Assert.Same(vm.AddFieldCommand, add.Command);
            Assert.Same(vm, add.DataContext);
            var columnsCard = Assert.IsType<SectionCard>(page.FindEditorElement<SectionCard>("ExpertColumnsGroupCard"));
            Assert.Equal(new Thickness(1), columnsCard.BorderThickness);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));

            list.ScrollIntoView(entries[fieldIndex]); Pump(); host.UpdateLayout(); Pump();
            var single = Assert.IsType<RadioButton>(page.FindEditorElement<RadioButton>("ExpertSingleGroupRadio"));
            var combined = Assert.IsType<RadioButton>(page.FindEditorElement<RadioButton>("ExpertCombinedGroupsRadio"));
            var singleInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertSingleGroupInput"));
            var combinedInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertCombinedGroupsInput"));
            var dateInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertDateInputFormatPanel"));
            var dateDisplay = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertDateDisplayFormatPanel"));
            var fieldCard = Assert.IsType<SectionCard>(page.FindEditorElement<SectionCard>("ExpertResultColumnCard"));
            var basics = Assert.IsType<WrapPanel>(page.FindEditorElement<WrapPanel>("ExpertColumnBasics"));
            Assert.Equal(new Thickness(1), fieldCard.BorderThickness);
            Assert.InRange(fieldCard.ActualHeight, 1, 300);
            Assert.All(basics.Children.OfType<StackPanel>(), block => Assert.InRange(block.Width, 1, 160));
            Assert.Same(field.SelectSingleCaptureGroupCommand, single.Command);
            Assert.Same(field.SelectCombinedCaptureGroupsCommand, combined.Command);
            // A named global radio group would incorrectly link different rows.
            Assert.Empty(single.GroupName);
            Assert.Empty(combined.GroupName);
            Assert.True(single.IsChecked);
            Assert.False(combined.IsChecked);
            Assert.Equal(Visibility.Visible, singleInput.Visibility);
            Assert.Equal(Visibility.Collapsed, combinedInput.Visibility);

            combined.Command!.Execute(null); Pump();
            Assert.True(combined.IsChecked);
            Assert.False(single.IsChecked);
            Assert.Empty(field.GroupName);
            Assert.Equal(Visibility.Collapsed, singleInput.Visibility);
            Assert.Equal(Visibility.Visible, combinedInput.Visibility);
            field.GroupNamesText = "prefix, number";
            single.Command!.Execute(null); Pump();
            Assert.Empty(field.GroupNamesText);
            Assert.Equal(Visibility.Visible, singleInput.Visibility);
            Assert.Equal(Visibility.Collapsed, combinedInput.Visibility);

            foreach (var kind in Enum.GetValues<ProfileFieldValueKind>())
            {
                field.Kind = kind; Pump(); host.UpdateLayout();
                var expected = kind == ProfileFieldValueKind.DateTime ? Visibility.Visible : Visibility.Collapsed;
                Assert.Equal(expected, dateInput.Visibility);
                Assert.Equal(expected, dateDisplay.Visibility);
            }

            field.Kind = ProfileFieldValueKind.DateTime;
            host.Width = 760; host.UpdateLayout(); Pump();
            list.ScrollIntoView(entries[fieldIndex]); Pump(); host.UpdateLayout();
            var groupEditor = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertGroupEditor"));
            Assert.True(groupEditor.ActualWidth <= fieldCard.ActualWidth);
            var bounds = groupEditor.TransformToAncestor(page).TransformBounds(new Rect(groupEditor.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth,
                $"The group editor must fit the narrow viewport: {bounds}, page width {page.ActualWidth}.");
            foreach (var block in basics.Children.OfType<FrameworkElement>()) AssertFitsWidth(block, fieldCard);
            foreach (var block in Assert.IsType<WrapPanel>(groupEditor.Children[0]).Children.OfType<FrameworkElement>().Where(block => block.IsVisible))
                AssertFitsWidth(block, fieldCard);
            Assert.InRange(fieldCard.ActualHeight, 1, 420);
            capture?.Invoke(page, "Profiles-expert-column-editor-small.png");

            list.ScrollIntoView(entries[rulesIndex]); Pump(); host.UpdateLayout(); Pump();
            var rulesCard = Assert.IsType<SectionCard>(page.FindEditorElement<SectionCard>("ExpertRulesGroupCard"));
            var addRule = Assert.IsType<Ui.Button>(page.FindEditorElement<Ui.Button>("ExpertAddRuleButton"));
            Assert.Equal(new Thickness(1), rulesCard.BorderThickness);
            Assert.Same(vm.AddRuleCommand, addRule.Command);
            Assert.Same(vm, addRule.DataContext);

            var rule = Assert.Single(vm.DraftRules);
            var ruleEntry = Assert.Single(entries, entry => ReferenceEquals(entry.Value, rule));
            list.ScrollIntoView(ruleEntry); Pump(); host.UpdateLayout(); Pump();
            var ruleCard = Assert.IsType<SectionCard>(page.FindEditorElement<SectionCard>("ExpertRegexRuleCard"));
            var options = Assert.IsType<WrapPanel>(page.FindEditorElement<WrapPanel>("ExpertRuleOptions"));
            var pattern = Assert.IsType<Ui.TextBox>(page.FindEditorElement<Ui.TextBox>("ExpertRegexPatternEditor"));
            Assert.Equal(new Thickness(1), ruleCard.BorderThickness);
            Assert.InRange(ruleCard.ActualHeight, 1, 380);
            Assert.All(options.Children.OfType<StackPanel>(), block => Assert.InRange(block.Width, 1, 220));
            foreach (var block in options.Children.OfType<FrameworkElement>()) AssertFitsWidth(block, ruleCard);
            AssertFitsWidth(pattern, ruleCard);
            Assert.True(pattern.ActualWidth >= ruleCard.ActualWidth * 0.8,
                "Regex patterns need the usable card width instead of a narrow fixed-width field.");
            Assert.Equal(160, pattern.MaxHeight);
            Assert.Equal(ScrollBarVisibility.Auto, pattern.VerticalScrollBarVisibility);
            capture?.Invoke(page, "Profiles-expert-rule-editor-small.png");
        }
        finally { host.Close(); }
    }

    private static void AssertFitsWidth(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
        Assert.True(element.ActualWidth > 0 && bounds.Left >= -1 && bounds.Right <= parent.ActualWidth + 1,
            $"{element.GetType().Name} must fit the compact card: {bounds}, card width {parent.ActualWidth}.");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
