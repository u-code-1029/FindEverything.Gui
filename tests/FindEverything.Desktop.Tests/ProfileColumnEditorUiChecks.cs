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

            list.ScrollIntoView(entries[fieldIndex]); Pump(); host.UpdateLayout(); Pump();
            var single = Assert.IsType<RadioButton>(page.FindEditorElement<RadioButton>("ExpertSingleGroupRadio"));
            var combined = Assert.IsType<RadioButton>(page.FindEditorElement<RadioButton>("ExpertCombinedGroupsRadio"));
            var singleInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertSingleGroupInput"));
            var combinedInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertCombinedGroupsInput"));
            var dateInput = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertDateInputFormatPanel"));
            var dateDisplay = Assert.IsType<StackPanel>(page.FindEditorElement<StackPanel>("ExpertDateDisplayFormatPanel"));
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
            Assert.True(groupEditor.ActualWidth <= 240);
            var bounds = groupEditor.TransformToAncestor(page).TransformBounds(new Rect(groupEditor.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth,
                $"The group editor must fit the narrow viewport: {bounds}, page width {page.ActualWidth}.");
            capture?.Invoke(page, "Profiles-expert-column-editor-small.png");
        }
        finally { host.Close(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
