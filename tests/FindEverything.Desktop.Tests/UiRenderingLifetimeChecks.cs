using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the composition test's one existing application dispatcher.</summary>
internal static class UiRenderingLifetimeChecks
{
    public static void Verify()
    {
        VerifyLocalizationDoesNotReadVirtualRows();
        VerifyEditorEntryTemplateKeepsMetadataContext();
        VerifyEntryPresenterChangesTemplateAtomically();
        VerifyOnlyTheActiveProfileStepIsConstructed();
        VerifyProfileCardsRecycleWithoutLosingDraftEdits();
    }

    private static void VerifyEntryPresenterChangesTemplateAtomically()
    {
        var fieldCommand = new RelayCommand<ProfileFieldDraftViewModel>(_ => { });
        var ruleCommand = new RelayCommand<ProfileRuleDraftViewModel>(_ => { });
        DataTemplate Template(System.Windows.Input.ICommand command)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(Button.CommandProperty, command);
            button.SetBinding(Button.CommandParameterProperty, new Binding());
            return new DataTemplate { VisualTree = button };
        }
        var fields = new ProfileEditorSection { Template = Template(fieldCommand) };
        var rules = new ProfileEditorSection { Template = Template(ruleCommand) };
        var first = new ProfileFieldDraftViewModel { Header = "First" };
        var second = new ProfileFieldDraftViewModel { Header = "Second" };
        var rule = new ProfileRuleDraftViewModel();
        var presenter = new ProfileEditorEntryPresenter();
        presenter.SetBinding(ProfileEditorEntryPresenter.EntryProperty, new Binding());
        presenter.DataContext = new ProfileEditorEntry(fields, first);
        var window = new Window { Content = presenter, Width = 500, Height = 250, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            var firstRoot = Assert.IsType<Button>(presenter.Child);
            Assert.Same(first, firstRoot.CommandParameter);
            presenter.DataContext = new ProfileEditorEntry(fields, second);
            Pump();
            Assert.Same(firstRoot, presenter.Child);
            Assert.Same(second, firstRoot.CommandParameter);

            // Match WPF recycling: inherited row context changes before the
            // entry binding is adopted. An old field command must never see a rule.
            presenter.DataContext = new ProfileEditorEntry(rules, rule);
            Pump(); window.UpdateLayout(); Pump();
            var ruleRoot = Assert.IsType<Button>(presenter.Child);
            Assert.NotSame(firstRoot, ruleRoot);
            Assert.Same(second, firstRoot.DataContext);
            Assert.Same(second, firstRoot.CommandParameter);
            Assert.Same(rule, ruleRoot.CommandParameter);

            presenter.DataContext = new ProfileEditorEntry(fields, first);
            Pump();
            Assert.Same(first, Assert.IsType<Button>(presenter.Child).CommandParameter);
            Assert.Same(rule, ruleRoot.DataContext);
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyEditorEntryTemplateKeepsMetadataContext()
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        var resourcePage = new ProfilesPage(vm);
        var list = new ProfileEditorList { Style = (Style)resourcePage.Resources["ProfileEditorVirtualListStyle"] };
        var field = new ProfileFieldDraftViewModel { Header = "Payload, not template metadata" };
        var cardFactory = new FrameworkElementFactory(typeof(SectionCard));
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProfileFieldDraftViewModel.Header)));
        cardFactory.AppendChild(textFactory);
        var template = new DataTemplate { VisualTree = cardFactory };
        Assert.False(template.IsSealed); // The presenter supports code-created templates too.
        list.Sections.Add(new ProfileEditorSection
        {
            IsCollection = true, ItemsSource = new[] { field },
            Template = template,
        });
        var window = new Window { Content = list, Width = 700, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            Assert.Single(list.Items);
            Assert.True(template.IsSealed);
            var card = Assert.Single(Descendants<SectionCard>(list));
            Assert.Same(field, card.DataContext);
            Assert.Contains(Descendants<TextBlock>(card), text => text.Text == field.Header);
        }
        finally { window.Close(); Pump(); }
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
            page.ActiveEditorList!.ScrollToSection("UserInputFields");
            Pump(); window.UpdateLayout(); Pump();
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
            page.ActiveEditorList!.ScrollToSection("UserInputFields");
            Pump(); window.UpdateLayout(); Pump();
            var restored = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));
            Assert.NotSame(dataSources, restored);
            Assert.Same(field, Assert.Single(vm.DraftUserInputFields));
            Assert.Equal("Unsaved draft must survive step views", vm.DraftDisplayName);
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyProfileCardsRecycleWithoutLosingDraftEdits()
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        var page = new ProfilesPage(vm);
        var window = new Window { Content = page, Width = 1100, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            vm.EditorMode = ProfileEditorMode.Expert;
            vm.DraftFields.Clear();
            for (var index = 0; index < 200; index++)
                vm.DraftFields.Add(new ProfileFieldDraftViewModel
                {
                    FieldId = "field-" + index, Header = "Column " + index, GroupName = "field" + index,
                });
            vm.CurrentEditorStep = ProfileEditorStep.PathAndFields;
            Pump(); window.UpdateLayout(); Pump();
            var list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            var entries = list.Items.Cast<ProfileEditorEntry>().ToArray();
            var first = entries.Single(entry => ReferenceEquals(entry.Value, vm.DraftFields[0]));
            var last = entries.Single(entry => ReferenceEquals(entry.Value, vm.DraftFields[^1]));
            Assert.Equal(200, entries.Count(entry => entry.Value is ProfileFieldDraftViewModel));
            Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 40);

            list.ScrollIntoView(first); Pump(); window.UpdateLayout(); Pump();
            var editor = Descendants<Wpf.Ui.Controls.TextBox>(list).Single(box =>
                ReferenceEquals(box.DataContext, first.Value)
                && box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Header");
            editor.SetCurrentValue(TextBox.TextProperty, "Edited through the visible control");
            editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Pump();
            Assert.Equal("Edited through the visible control", vm.DraftFields[0].Header);
            Assert.Same(first, list.Items.Cast<ProfileEditorEntry>().Single(entry => ReferenceEquals(entry.Value, first.Value)));

            list.ScrollIntoView(last); Pump(); window.UpdateLayout(); Pump();
            Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 40);
            Assert.Null(list.ItemContainerGenerator.ContainerFromItem(first));
            list.ScrollIntoView(first); Pump(); window.UpdateLayout(); Pump();
            var restored = Descendants<Wpf.Ui.Controls.TextBox>(list).Single(box =>
                ReferenceEquals(box.DataContext, first.Value)
                && box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Header");
            Assert.Equal("Edited through the visible control", restored.Text);
            Assert.Equal("Edited through the visible control", vm.DraftFields[0].Header);
            Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 40);
        }
        finally { window.Close(); Pump(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
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
