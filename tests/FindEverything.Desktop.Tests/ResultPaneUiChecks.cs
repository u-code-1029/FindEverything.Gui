using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs in the smoke test's existing shown window, before scan fixtures acquire an operation lease.</summary>
internal static class ResultPaneUiChecks
{
    public static void Verify(IServiceProvider services, MainWindow window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var previousPage = (Assert.IsType<Ui.NavigationView>(window.FindName("RootNavigation")).SelectedItem
            as Ui.NavigationViewItem)?.TargetPageType;
        var files = services.GetRequiredService<FilesPage>();
        var catalog = services.GetRequiredService<CatalogPage>();
        var filesVm = Assert.IsType<FileSearchViewModel>(files.DataContext);
        var catalogVm = Assert.IsType<CatalogViewModel>(catalog.DataContext);
        var width = window.Width;
        var height = window.Height;
        var filesDetails = filesVm.IsDetailsOpen;
        var filesFilters = filesVm.IsFilterOpen;
        var catalogDetails = catalogVm.IsDetailsOpen;
        var createdFilter = filesVm.SelectedCreatedDateFilter;
        try
        {
            window.Width = 1100;
            window.Height = 800;
            Assert.True(navigation.Navigate(typeof(FilesPage)));
            PumpUntil(window, () => files.IsLoaded && filesVm.CanEditFileFilters);
            using (var input = new NativePopupInput(window))
            {
                VerifyFilePanes(window, files, filesVm, input);
                VerifyNestedEscape(window, files, filesVm, input);
            }
            filesVm.IsFilterOpen = filesVm.IsDetailsOpen = false;
            VerifyFooterPresentation(window, files);

            Assert.True(navigation.Navigate(typeof(CatalogPage)));
            PumpUntil(window, () => catalog.IsLoaded && catalogVm.IsResultInteractionEnabled);
            using (var input = new NativePopupInput(window))
            {
                var opener = Named<Ui.Button>(catalog, "CatalogDataManagementButton");
                var close = Named<Ui.Button>(catalog, "CloseCatalogDetailsButton");
                catalogVm.IsDetailsOpen = false;
                input.Click(opener);
                PumpUntil(window, () => catalogVm.IsDetailsOpen && close.IsKeyboardFocusWithin);
                input.Key(0x1B);
                PumpUntil(window, () => !catalogVm.IsDetailsOpen && opener.IsKeyboardFocusWithin);
                input.Click(opener);
                PumpUntil(window, () => close.IsVisible);
                input.Click(close);
                PumpUntil(window, () => !catalogVm.IsDetailsOpen && opener.IsKeyboardFocusWithin);
            }
            VerifyFooterPresentation(window, catalog);
        }
        finally
        {
            // A failed native-popup assertion must not leave an HWND/capture open
            // for later smoke cases, or leak the fixture's date filter and size.
            foreach (var combo in Descendants<ComboBox>(files)) combo.IsDropDownOpen = false;
            foreach (var picker in Descendants<Ui.CalendarDatePicker>(files))
                picker.SetCurrentValue(Ui.CalendarDatePicker.IsCalendarOpenProperty, false);
            filesVm.SelectedCreatedDateFilter = createdFilter;
            filesVm.IsDetailsOpen = filesDetails;
            filesVm.IsFilterOpen = filesFilters;
            catalogVm.IsDetailsOpen = catalogDetails;
            window.Width = width;
            window.Height = height;
            navigation.Navigate(previousPage ?? typeof(HomePage));
            Pump(window);
        }
    }

    private static void VerifyFilePanes(Window window, FilesPage page, FileSearchViewModel vm, NativePopupInput input)
    {
        var details = Named<SectionCard>(page, "FileDetailsPanel");
        var filters = Named<SectionCard>(page, "FileFiltersPanel");
        var detailsOpener = Named<Ui.Button>(page, "FileDataManagementButton");
        var filtersOpener = Named<Ui.Button>(page, "FilterButton");
        var closeDetails = Named<Ui.Button>(page, "CloseFileDetailsButton");
        var closeFilters = Named<Ui.Button>(page, "CloseFileFiltersButton");
        vm.IsDetailsOpen = vm.IsFilterOpen = false;
        input.Click(detailsOpener);
        PumpUntil(window, () => vm.IsDetailsOpen && closeDetails.IsKeyboardFocusWithin);
        Assert.False(vm.IsFilterOpen);
        vm.CloseFileFiltersCommand.Execute(null);
        Assert.True(vm.IsDetailsOpen, "Closing the inactive filter pane must not dismiss data management.");
        input.Click(filtersOpener);
        PumpUntil(window, () => vm.IsFilterOpen && closeFilters.IsKeyboardFocusWithin);
        Assert.False(vm.IsDetailsOpen);
        Assert.False(details.IsVisible);
        vm.CloseDetailsCommand.Execute(null);
        Assert.True(vm.IsFilterOpen, "Closing inactive data management must not dismiss filters.");

        Assert.Contains(Named<ComboBox>(page, "FileKindComboBox"), Descendants<ComboBox>(filters));
        Assert.Contains(Named<ComboBox>(page, "FileSortComboBox"), Descendants<ComboBox>(filters));
        Assert.DoesNotContain(Named<ComboBox>(page, "FileKindComboBox"), Descendants<ComboBox>(details));
        Assert.DoesNotContain(Named<Ui.TextBox>(page, "PartialRefreshPathTextBox"), Descendants<Ui.TextBox>(filters));

        // The production shell minimum is 760; its collapsed navigation leaves
        // roughly a 660-DIP page workspace. Do not pretend a coerced width is 660.
        window.Width = 760;
        window.Height = 600;
        Pump(window);
        AssertInside(filters, page, "Narrow file filters must stay inside the page.");
        AssertInside(closeFilters, page, "The narrow pane close action must remain reachable.");
        input.Click(closeFilters);
        PumpUntil(window, () => !vm.IsFilterOpen && filtersOpener.IsKeyboardFocusWithin);
        input.Click(detailsOpener);
        PumpUntil(window, () => vm.IsDetailsOpen && closeDetails.IsKeyboardFocusWithin);
        AssertInside(details, page, "Narrow data management must stay inside the page.");
        input.Click(closeDetails);
        PumpUntil(window, () => !vm.IsDetailsOpen && detailsOpener.IsKeyboardFocusWithin);
        input.Click(detailsOpener);
        PumpUntil(window, () => vm.IsDetailsOpen && closeDetails.IsKeyboardFocusWithin);
        input.Key(0x1B);
        PumpUntil(window, () => !vm.IsDetailsOpen && detailsOpener.IsKeyboardFocusWithin);
    }

    private static void VerifyNestedEscape(Window window, FilesPage page, FileSearchViewModel vm, NativePopupInput input)
    {
        var opener = Named<Ui.Button>(page, "FilterButton");
        input.Click(opener);
        PumpUntil(window, () => vm.IsFilterOpen);
        var combo = Named<ComboBox>(page, "FileKindComboBox");
        combo.BringIntoView(); Pump(window);
        input.Click(combo);
        PumpUntil(window, () => combo.IsDropDownOpen);
        input.Key(0x1B);
        PumpUntil(window, () => !combo.IsDropDownOpen);
        Assert.True(vm.IsFilterOpen, "The first Escape closes only the nested ComboBox.");
        input.Key(0x1B);
        PumpUntil(window, () => !vm.IsFilterOpen && opener.IsKeyboardFocusWithin);

        input.Click(opener);
        PumpUntil(window, () => vm.IsFilterOpen);
        vm.SelectedCreatedDateFilter = vm.DateFilterChoices.Single(choice => choice.Value == DateFilterPreset.Custom);
        Pump(window);
        var picker = Descendants<Ui.CalendarDatePicker>(Named<SectionCard>(page, "FileFiltersPanel"))
            .First(candidate => candidate.IsVisible);
        picker.BringIntoView(); Pump(window);
        input.Click(picker);
        PumpUntil(window, () => picker.IsCalendarOpen);
        input.Key(0x1B);
        PumpUntil(window, () => !picker.IsCalendarOpen);
        Assert.True(vm.IsFilterOpen, "The first Escape closes only the nested calendar.");
        Assert.True(picker.IsKeyboardFocusWithin, "Calendar dismissal must return focus to its date control.");
        input.Key(0x1B);
        PumpUntil(window, () => !vm.IsFilterOpen && opener.IsKeyboardFocusWithin);
    }

    private static void VerifyFooterPresentation(Window window, Page page)
    {
        var toggle = Named<ToggleButton>(page, "SelectedOnlyButton");
        var copy = Named<Ui.SplitButton>(page, "CopySelectionButton");
        var toolbar = Named<SectionCard>(page, "SelectionToolbar");
        var toggleContext = toggle.ReadLocalValue(FrameworkElement.DataContextProperty);
        var copyContext = copy.ReadLocalValue(FrameworkElement.DataContextProperty);
        var fixture = new FooterFixture();
        var template = copy.ContentTemplate;
        Assert.Same(page.FindResource("SelectionCopyContentTemplate"), template);
        Assert.Equal("SelectedOutputFormat.DisplayName", BindingOperations.GetBinding(copy, ContentControl.ContentProperty)?.Path.Path);
        try
        {
            toggle.DataContext = copy.DataContext = fixture;
            toolbar.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            fixture.HasSelection = true;
            foreach (var size in new[] { new Size(1360, 860), new Size(760, 600), new Size(1360, 860) })
            {
                window.Width = size.Width; window.Height = size.Height; Pump(window);
                Assert.Same(template, copy.ContentTemplate);
                Assert.Equal("SelectedOutputFormat.DisplayName", BindingOperations.GetBinding(copy, ContentControl.ContentProperty)?.Path.Path);
                Assert.Equal(fixture.SelectedOutputFormat.DisplayName, copy.Content);
                Assert.Contains(Descendants<TextBlock>(copy), text => text.Text == fixture.SelectedOutputFormat.DisplayName);
                AssertInside(copy, page, "The copy action and active format must remain reachable after resizing.");
            }
            fixture.SelectedOutputFormat = new("QA format B");
            Pump(window);
            Assert.Equal("QA format B", copy.Content);
            fixture.IsShowingSelectedOnly = true;
            fixture.HasSelection = false;
            Pump(window);
            Assert.True(toggle.IsVisible && toggle.IsEnabled && toggle.IsChecked == true,
                "Review must retain an enabled exit toggle after its final selected row is unchecked.");
            using var input = new NativePopupInput(window);
            input.Click(toggle);
            PumpUntil(window, () => !fixture.IsShowingSelectedOnly);
            Assert.Equal(Visibility.Collapsed, toggle.Visibility);
        }
        finally
        {
            RestoreContext(toggle, toggleContext); RestoreContext(copy, copyContext);
            BindingOperations.GetBindingExpression(toolbar, UIElement.VisibilityProperty)?.UpdateTarget();
            Pump(window);
        }
    }

    private static void RestoreContext(FrameworkElement element, object value)
    {
        if (value == DependencyProperty.UnsetValue) element.ClearValue(FrameworkElement.DataContextProperty);
        else element.SetValue(FrameworkElement.DataContextProperty, value);
    }

    private static T Named<T>(FrameworkElement owner, string name) where T : FrameworkElement => Assert.IsType<T>(owner.FindName(name));

    private static void AssertInside(FrameworkElement element, FrameworkElement viewport, string message)
    {
        var origin = element.TranslatePoint(new Point(), viewport);
        Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, message);
        Assert.True(origin.X >= -1 && origin.Y >= -1
            && origin.X + element.ActualWidth <= viewport.ActualWidth + 1
            && origin.Y + element.ActualHeight <= viewport.ActualHeight + 1, message);
    }

    private static void PumpUntil(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        do { Pump(window); if (condition()) return; } while (DateTime.UtcNow < deadline);
        Assert.True(condition(), "The native result-workspace interaction did not settle.");
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        window.UpdateLayout();
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

    public sealed record FormatFixture(string DisplayName);

    public sealed class FooterFixture : ObservableObject
    {
        private bool _hasSelection;
        private bool _isShowingSelectedOnly;
        private FormatFixture _selectedOutputFormat = new("QA format A");
        public FooterFixture() => ToggleSelectedOnlyCommand = new RelayCommand(() => IsShowingSelectedOnly = !IsShowingSelectedOnly);
        public bool HasSelection
        {
            get => _hasSelection;
            set { SetProperty(ref _hasSelection, value); OnPropertyChanged(nameof(CanShowSelectedOnly)); }
        }
        public bool IsShowingSelectedOnly
        {
            get => _isShowingSelectedOnly;
            set { SetProperty(ref _isShowingSelectedOnly, value); OnPropertyChanged(nameof(CanShowSelectedOnly)); }
        }
        public bool CanShowSelectedOnly => HasSelection || IsShowingSelectedOnly;
        public FormatFixture SelectedOutputFormat { get => _selectedOutputFormat; set => SetProperty(ref _selectedOutputFormat, value); }
        public IRelayCommand ToggleSelectedOnlyCommand { get; }
        public IRelayCommand CopySelectionCommand { get; } = new RelayCommand(static () => { });
    }
}
