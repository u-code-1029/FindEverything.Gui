using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Runtime;
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
        VerifyPersistentOutputPreviewSurvivesWorkspaceResize();
        VerifyProfileCardsRecycleWithoutLosingDraftEdits();
        VerifyReadOnlyColumnsUseTheNarrowestBinding();
        VerifySizingCommitsAfterTheWholeSample();
        VerifyIdleTextCellsDoNotCreateNativeTextViews();
        VerifyPlainHighlightingKeepsTheSimpleTextPath();
    }

    private static void VerifyPersistentOutputPreviewSurvivesWorkspaceResize()
    {
        var editor = new Wpf.Ui.Controls.TextBox();
        editor.SetBinding(TextBox.TextProperty, new Binding("Template") { Mode = BindingMode.OneWay });
        var preview = new Wpf.Ui.Controls.TextBox { IsReadOnly = true };
        preview.SetBinding(TextBox.TextProperty, new Binding("Preview") { Mode = BindingMode.OneWay });
        var previewCard = new SectionCard { Header = preview };
        var workspace = new SplitWorkspace
        {
            KeepSecondaryVisible = true,
            PrimaryContent = editor,
            SecondaryContent = previewCard,
            DataContext = new { Template = "{Name}", Preview = "Apollo" },
        };
        foreach (var width in new[] { 1100d, 680d, 1100d, 680d })
        {
            workspace.Measure(new Size(width, 480));
            workspace.Arrange(new Rect(0, 0, width, 480));
            workspace.UpdateLayout(); Pump(); workspace.UpdateLayout();
            Assert.Same(editor, workspace.PrimaryContent);
            Assert.Same(previewCard, workspace.SecondaryContent);
            Assert.Same(preview, previewCard.Header);
            Assert.Equal("{Name}", editor.Text);
            Assert.Equal("Apollo", preview.Text);
            Assert.Equal(BindingStatus.Active, preview.GetBindingExpression(TextBox.TextProperty)!.Status);
            Assert.Empty(Descendants<TabControl>(workspace));
            Assert.Equal(2, workspace.Children.Count);
            Assert.True(editor.ActualHeight > 0 && previewCard.ActualHeight > 0);
            Assert.Same(previewCard, LogicalTreeHelper.GetParent(preview));
            Assert.Equal(width < 900 ? 2 : 0, Grid.GetRow(previewCard));
            Assert.Equal(width < 900 ? 0 : 2, Grid.GetColumn(previewCard));
        }
    }

    private static void VerifyIdleTextCellsDoNotCreateNativeTextViews()
    {
        // No HWND/pointer is needed: measuring an untouched result must not
        // instantiate ScrollViewer/TextBoxView for its transparent input target.
        var cell = new SelectableCatalogText(new Binding("Value") { Mode = BindingMode.OneWay })
        {
            DataContext = new { Value = "Accessible while idle" },
        };
        // The display binding is created before Highlight joins the logical
        // tree, and TextBox subscribes to that display DP through a second
        // OneWay binding. Measuring alone does not drain their initial DataBind
        // work; wait for the same dispatcher turn a displayed cell receives.
        Pump();
        cell.Measure(new Size(300, 40)); cell.Arrange(new Rect(0, 0, 300, 40));
        var native = Assert.Single(cell.Children.OfType<Wpf.Ui.Controls.TextBox>());
        native.ApplyTemplate();
        Pump();
        Assert.Equal(BindingStatus.Active, cell.Highlight.GetBindingExpression(TextHighlighting.DisplayTextProperty)!.Status);
        Assert.Equal(BindingStatus.Active, native.GetBindingExpression(TextBox.TextProperty)!.Status);
        Assert.Equal("Accessible while idle", TextHighlighting.GetDisplayText(cell.Highlight));
        Assert.False(cell.IsNativeEditorPrepared);
        Assert.Empty(Descendants<ScrollViewer>(cell));
        Assert.Equal("Accessible while idle", native.Text);
        var peer = UIElementAutomationPeer.CreatePeerForElement(native)!;
        var provider = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
        Assert.True(provider.IsReadOnly);
        Assert.Equal("Accessible while idle", provider.Value);

        cell.DataContext = new { Value = "Recycled while idle" };
        Pump();
        Assert.Equal("Recycled while idle", native.Text);
        Assert.False(cell.IsNativeEditorPrepared);
        Assert.Empty(Descendants<ScrollViewer>(cell));
        Assert.Same(native, cell.TextBox);
        Assert.True(cell.IsNativeEditorPrepared);
        Assert.Single(Descendants<ScrollViewer>(cell));
    }

    private static void VerifyPlainHighlightingKeepsTheSimpleTextPath()
    {
        var text = new Wpf.Ui.Controls.TextBlock();
        // This WPF implementation field detects accidental Inlines access,
        // which would make an otherwise plain cell allocate a rich text tree.
        var complexContent = typeof(TextBlock).GetField("_complexContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(complexContent);
        TextHighlighting.SetDisplayText(text, "Apollo archive");
        Assert.Equal("Apollo archive", text.Text);
        Assert.Null(complexContent.GetValue(text));
        TextHighlighting.SetHighlightText(text, "unmatched");
        TextHighlighting.SetDisplayText(text, "Another archive");
        Assert.Null(complexContent.GetValue(text));
        TextHighlighting.SetHighlightText(text, "archive");
        Assert.NotNull(complexContent.GetValue(text));
        Assert.Single(text.Inlines.OfType<System.Windows.Documents.Run>(), run => run.Background is not null);
        TextHighlighting.SetHighlightText(text, string.Empty);
        Assert.Equal("Another archive", text.Text);
        Assert.DoesNotContain(text.Inlines.OfType<System.Windows.Documents.Run>(), run => run.Background is not null);
    }

    private static void VerifySizingCommitsAfterTheWholeSample()
    {
        var fields = Enumerable.Range(0, 8).Select(i => new ProfileFieldDescriptor($"f{i}", $"f{i}",
            $"Column {i}", i, false, ProfileFieldValueKind.String, true, null, null)).ToArray();
        CatalogItemViewModel[] Rows(bool longValues) => Enumerable.Range(0, 200).Select(i =>
            new CatalogItemViewModel(new CatalogItem($@"C:\Archive\{i}", i.ToString(), "sample", new object(),
                fields.ToDictionary(field => field.FieldId, field => (object?)(longValues
                    ? $"{field.FieldId}-{i}-" + new string('W', 400) : "Short")), false),
                fields, new TestAppLocalizer())).ToArray();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = Rows(false),
            DataContext = new { AppliedHighlightQuery = string.Empty },
        };
        DynamicProfileGrid.SetFields(grid, fields);
        var sizingProperty = (DependencyProperty)typeof(DynamicProfileGrid)
            .GetField("SizingProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var manualProperty = (DependencyProperty)typeof(DynamicProfileGrid)
            .GetField("IsManualWidthProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var state = grid.GetValue(sizingProperty);
        var measurementField = state.GetType().GetField("Measurement")!;
        var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))!;
        var handlers = new List<(DataGridColumn Column, EventHandler Handler)>();
        var changedDuringMeasurement = false;
        var changes = new Dictionary<DataGridColumn, int>();
        var window = new Window { Content = grid, Width = 1000, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            var manual = grid.Columns[2];
            manual.SetValue(manualProperty, true);
            manual.Width = new DataGridLength(257);
            foreach (var column in grid.Columns.Skip(3))
            {
                column.Width = new DataGridLength(Math.Max(column.MinWidth, 128));
                var lastWidth = column.Width.Value;
                EventHandler handler = (_, _) =>
                {
                    var width = column.Width.Value;
                    if (Math.Abs(width - lastWidth) < 0.5) return;
                    lastWidth = width;
                    changes[column] = changes.GetValueOrDefault(column) + 1;
                    changedDuringMeasurement |= measurementField.GetValue(state) is not null;
                };
                descriptor.AddValueChanged(column, handler);
                handlers.Add((column, handler));
            }
            grid.ItemsSource = Rows(true);
            Pump(); window.UpdateLayout(); Pump();
            Assert.Null(measurementField.GetValue(state));
            Assert.False(changedDuringMeasurement);
            Assert.NotEmpty(changes);
            Assert.All(changes.Values, count => Assert.Equal(1, count));
            Assert.Equal(257, manual.Width.Value);
            foreach (var (column, handler) in handlers) descriptor.RemoveValueChanged(column, handler);
            handlers.Clear();
            window.Width = 760; Pump(); window.UpdateLayout(); Pump();
            Assert.Equal(257, manual.Width.Value);
        }
        finally
        {
            foreach (var (column, handler) in handlers) descriptor.RemoveValueChanged(column, handler);
            window.Close(); Pump();
        }
    }

    private static void VerifyReadOnlyColumnsUseTheNarrowestBinding()
    {
        ProfileFieldDescriptor Field(string id) => new(id, id, id, 0, false,
            ProfileFieldValueKind.String, true, null, null);
        var fields = new[]
        {
            Field("plain"),
            Field("alias") with { ValueMappings = new Dictionary<string, string> { ["A"] = "Mapped alias" } },
        };
        var row = new CatalogItemViewModel(new CatalogItem(@"C:\Archive", "Archive", "sample", new object(),
            new Dictionary<string, object?> { ["plain"] = "Unmapped value", ["alias"] = "A" }, false),
            fields, new TestAppLocalizer());
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = new[] { row },
            DataContext = new { AppliedHighlightQuery = string.Empty },
        };
        DynamicProfileGrid.SetFields(grid, fields);
        var window = new Window { Content = grid, Width = 1000, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            var cells = Descendants<SelectableCatalogText>(grid).ToArray();
            var plain = Assert.Single(cells, cell => TextHighlighting.GetDisplayText(cell.Highlight) == "Unmapped value");
            var mapped = Assert.Single(cells, cell => TextHighlighting.GetDisplayText(cell.Highlight) == "Mapped alias");
            AssertPath(plain, "DisplayValues[plain]");
            AssertPath(mapped, "DisplayCells[alias].Value");
            var plainHost = Assert.IsAssignableFrom<Decorator>(VisualTreeHelper.GetParent(plain));
            Assert.Same(plain, plainHost.Child);
            Assert.Empty(Descendants<Wpf.Ui.Controls.SymbolIcon>(plainHost));
            var errorRow = new CatalogItemViewModel(new CatalogItem(@"C:\Archive", "Archive", "sample", new object(),
                new Dictionary<string, object?> { ["plain"] = "Unmapped value", ["alias"] = "A" }, false)
                { FieldErrors = new Dictionary<string, string> { ["plain"] = "Could not read this field" } },
                fields, new TestAppLocalizer());
            plainHost.DataContext = errorRow; Pump();
            var warning = Assert.Single(Descendants<Wpf.Ui.Controls.SymbolIcon>(plainHost));
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Warning20, warning.Symbol);
            Assert.Equal("Could not read this field", warning.ToolTip);
            Assert.Equal("Could not read this field", System.Windows.Automation.AutomationProperties.GetName(warning));
            plainHost.DataContext = row; Pump();
            Assert.Same(plain, plainHost.Child);
            Assert.Empty(Descendants<Wpf.Ui.Controls.SymbolIcon>(plainHost));
            row.SetShowOriginalValues(true); Pump();
            Assert.Equal("Unmapped value", plain.TextBox.Text);
            Assert.Equal("A", mapped.TextBox.Text);
            Assert.Equal("A", mapped.ToolTip);
            row.SetShowOriginalValues(false); Pump();
            Assert.Equal("Mapped alias", mapped.TextBox.Text);
            Assert.Equal("Mapped alias", mapped.ToolTip);
        }
        finally { window.Close(); Pump(); }

        static void AssertPath(SelectableCatalogText cell, string expected)
        {
            Assert.Equal(expected, cell.Highlight.GetBindingExpression(TextHighlighting.DisplayTextProperty)!.ParentBinding.Path.Path);
            Assert.Equal(expected, cell.GetBindingExpression(FrameworkElement.ToolTipProperty)!.ParentBinding.Path.Path);
        }
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
            var generatedRule = Assert.IsAssignableFrom<Expander>(page.FindEditorElement<FrameworkElement>("GeneratedPathRuleExpander"));
            Assert.False(generatedRule.IsExpanded);
            generatedRule.IsExpanded = true;
            Pump(); window.UpdateLayout(); Pump();
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
