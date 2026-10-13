using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Pages;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the composition test's one existing application dispatcher.</summary>
internal static class UiRenderingLifetimeChecks
{
    public static void Verify()
    {
        VerifyLocalizationDoesNotReadVirtualRows();
        VerifyOnlyTheActiveProfileStepIsConstructed();
    }

    private static void VerifyLocalizationDoesNotReadVirtualRows()
    {
        var rows = new ObservedVirtualList();
        var list = new ItemsControl { ItemsSource = rows };
        var before = rows.Reads;
        LocalizationScope.SetIsEnabled(list, true);
        Assert.Equal(before, rows.Reads);

        var child = new TextBlock { Text = "Untranslated data is not a caption" };
        var parent = new StackPanel();
        parent.Children.Add(child);
        LocalizationScope.SetIsEnabled(parent, true);
        Assert.False(LocalizationScope.GetIsEnabled(child));
        Assert.Equal("Untranslated data is not a caption", child.Text);
    }

    private static void VerifyOnlyTheActiveProfileStepIsConstructed()
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        var page = new ProfilesPage(vm);
        var window = new Window { Content = page, Width = 1100, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            var profilePicker = Assert.IsType<ComboBox>(page.FindEditorElement<ComboBox>("EditableProfileComboBox"));
            Assert.Null(page.FindEditorElement<FrameworkElement>("ProfilePathTemplateTextBox"));
            Assert.Null(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));
            Assert.Null(page.FindEditorElement<FrameworkElement>("ProfileReviewPanel"));
            vm.DraftDisplayName = "Unsaved draft must survive step views";

            vm.CurrentEditorStep = ProfileEditorStep.PathAndFields;
            Pump(); window.UpdateLayout(); Pump();
            Assert.False(profilePicker.IsLoaded);
            var path = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindEditorElement<Wpf.Ui.Controls.TextBox>("ProfilePathTemplateTextBox"));
            Assert.True(path.IsReadOnly);
            Assert.Null(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));

            vm.CurrentEditorStep = ProfileEditorStep.DataSources;
            Pump(); window.UpdateLayout(); Pump();
            Assert.False(path.IsLoaded);
            var dataSources = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));
            vm.AddUserInputFieldCommand.Execute(null);
            var field = Assert.Single(vm.DraftUserInputFields);
            Assert.Null(page.FindEditorElement<FrameworkElement>("ProfilePathTemplateTextBox"));

            vm.CurrentEditorStep = ProfileEditorStep.Review;
            Pump(); window.UpdateLayout(); Pump();
            Assert.False(dataSources.IsLoaded);
            Assert.NotNull(page.FindEditorElement<FrameworkElement>("ProfileReviewPanel"));
            Assert.Null(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));

            vm.CurrentEditorStep = ProfileEditorStep.DataSources;
            Pump(); window.UpdateLayout(); Pump();
            var restored = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));
            Assert.NotSame(dataSources, restored);
            Assert.Same(field, Assert.Single(vm.DraftUserInputFields));
            Assert.Equal("Unsaved draft must survive step views", vm.DraftDisplayName);
        }
        finally { window.Close(); Pump(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class ObservedVirtualList : IList
    {
        public int Reads { get; private set; }
        public int Count => 10_000;
        public object? this[int index] { get { Reads++; return "Row " + index; } set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public IEnumerator GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
        public int IndexOf(object? value) => -1;
        public bool Contains(object? value) => false;
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
