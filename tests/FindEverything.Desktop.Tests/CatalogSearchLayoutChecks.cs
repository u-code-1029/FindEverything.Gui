using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Uses the composition smoke test's existing STA/application resources.</summary>
internal static class CatalogSearchLayoutChecks
{
    public static void Verify(IServiceProvider services, Action<FrameworkElement, string>? capture = null)
    {
        var page = new CatalogPage(services.GetRequiredService<CatalogViewModel>(),
            services.GetRequiredService<ScanConsoleViewModel>(), services.GetRequiredService<IGridLayoutStore>());
        var originalContext = page.DataContext;
        var console = services.GetRequiredService<ScanConsoleViewModel>();
        var wasConsoleOpen = console.IsPanelOpen;
        console.IsPanelOpen = false;
        var fixture = new LayoutFixture();
        page.DataContext = fixture;
        var host = new Window
        {
            Content = page, Width = 1140, Height = 790, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
        };
        try
        {
            host.Show();
            foreach (var size in new[] { new Size(1140, 790), new Size(660, 480) })
            {
                host.Width = size.Width; host.Height = size.Height;
                fixture.SetState(busy: false, pending: false, preserving: false, review: false);
                Pump(host);
                var rows = Named<Grid>(page, "CatalogCommandRows");
                Assert.Equal(2, rows.RowDefinitions.Count);
                Assert.Equal(44, rows.RowDefinitions[0].ActualHeight);
                Assert.Equal(40, rows.RowDefinitions[1].ActualHeight);
                var search = Named<Ui.TextBox>(page, "SearchBox");
                Assert.True(search.ActualHeight >= 44 && search.FontSize >= 17);
                Assert.True(search.ActualWidth >= 220, "Search must remain the largest primary control at the minimum viewport.");
                var profile = Named<ComboBox>(page, "CatalogProfileComboBox");
                var import = Named<Ui.Button>(page, "ImportCatalogProfileButton");
                Assert.Same(fixture.ImportProfileCommand, import.Command);
                Assert.Equal("ImportProfileCommand", BindingOperations.GetBinding(import, ButtonBase.CommandProperty)?.Path.Path);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(import)));
                Assert.NotNull(import.ToolTip);
                Assert.True(profile.ActualWidth >= 96, "Long profile names must not displace the JSON or location action.");
                foreach (var name in new[] { "SearchBox", "CatalogProfileComboBox", "ImportCatalogProfileButton", "CatalogLocationButton", "CatalogDataManagementButton", "CatalogWorkspaceTransferPanel" })
                    AssertInside(Named<FrameworkElement>(page, name), page);

                var commandHeight = Named<CommandStrip>(page, "CatalogCommandStrip").ActualHeight;
                var resultsTop = Named<Grid>(page, "CatalogResultsWorkspace").TranslatePoint(new Point(), page).Y;
                var searchWidth = search.ActualWidth;
                foreach (var state in new[]
                {
                    (Busy: true, Pending: false, Preserving: false, Review: false),
                    (Busy: false, Pending: true, Preserving: false, Review: false),
                    (Busy: false, Pending: false, Preserving: true, Review: false),
                    (Busy: false, Pending: false, Preserving: false, Review: true),
                    (Busy: true, Pending: true, Preserving: true, Review: true),
                })
                {
                    fixture.SetState(state.Busy, state.Pending, state.Preserving, state.Review);
                    Pump(host);
                    Assert.Equal(commandHeight, Named<CommandStrip>(page, "CatalogCommandStrip").ActualHeight);
                    Assert.Equal(resultsTop, Named<Grid>(page, "CatalogResultsWorkspace").TranslatePoint(new Point(), page).Y);
                    Assert.Equal(searchWidth, search.ActualWidth);
                    Assert.Equal(32, Named<Grid>(page, "CatalogStatusRow").ActualHeight);
                    var elapsed = Named<Ui.TextBlock>(page, "CatalogScanElapsedCaption");
                    Assert.Equal(fixture.LastCompletedScanElapsedText, elapsed.Text);
                    Assert.Equal(TextWrapping.NoWrap, elapsed.TextWrapping);
                    AssertInside(elapsed, page);
                    Assert.Equal("LastCompletedScanElapsedText", BindingOperations.GetBinding(elapsed, TextBlock.TextProperty)?.Path.Path);
                    foreach (var name in new[] { "CatalogProgressCaption", "CatalogStateCaption" })
                    {
                        var caption = Named<Ui.TextBlock>(page, name);
                        Assert.Equal(TextWrapping.NoWrap, caption.TextWrapping);
                        Assert.Equal(TextTrimming.CharacterEllipsis, caption.TextTrimming);
                        Assert.NotNull(caption.ToolTip);
                    }
                    Assert.Equal(state.Busy ? Visibility.Visible : Visibility.Collapsed,
                        Named<Ui.TextBlock>(page, "CatalogProgressCaption").Visibility);
                    Assert.Equal(state.Busy ? Visibility.Collapsed : Visibility.Visible,
                        Named<Ui.TextBlock>(page, "CatalogStateCaption").Visibility);
                }
                fixture.SetState(busy: true, pending: false, preserving: false, review: false);
                Pump(host);
                capture?.Invoke(page, $"CatalogPage-{(size.Width > 900 ? "wide" : "small")}-stable-search.png");
            }

            fixture.HasColumnFilters = true;
            fixture.Refresh(); Pump(host);
            var filters = Named<Ui.DynamicScrollViewer>(page, "CatalogFilterScroller");
            Assert.Equal(44, filters.ActualHeight);
            Assert.Equal(ScrollBarVisibility.Disabled, filters.VerticalScrollBarVisibility);
            Assert.True(filters.ScrollableWidth > 0, "Many filter chips must scroll horizontally instead of creating extra header rows.");
            filters.ScrollToHorizontalOffset(filters.ScrollableWidth); Pump(host);
            Assert.True(filters.HorizontalOffset > 0);

            fixture.HasSelection = true;
            fixture.SetState(busy: false, pending: false, preserving: false, review: false);
            console.IsPanelOpen = true;
            Pump(host);
            var panel = Named<ScanConsolePanel>(page, "ScanConsolePanel");
            Assert.True(Named<ListBox>(panel, "TraceList").ActualHeight >= 72,
                "Minimum-height page must keep several log rows even with filters and selection tools visible.");
            AssertInside(Named<FrameworkElement>(page, "SelectionToolbar"), page);
            AssertInside(Named<FrameworkElement>(page, "ConsoleSplitter"), page);
            AssertInside(panel, page);
            capture?.Invoke(page, "CatalogPage-minimum-height-log-selection.png");
        }
        finally
        {
            host.Content = null;
            host.Close();
            page.DataContext = originalContext;
            console.IsPanelOpen = wasConsoleOpen;
        }
    }

    private static T Named<T>(FrameworkElement owner, string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(owner.FindName(name));

    private static void AssertInside(FrameworkElement element, FrameworkElement viewport)
    {
        var origin = element.TranslatePoint(new Point(), viewport);
        Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, element.Name);
        Assert.True(origin.X >= -1 && origin.Y >= -1 && origin.X + element.ActualWidth <= viewport.ActualWidth + 1
            && origin.Y + element.ActualHeight <= viewport.ActualHeight + 1, $"{element.Name} must remain inside the viewport.");
    }

    private static void Pump(Window host)
    {
        host.UpdateLayout();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        host.UpdateLayout();
    }

    public sealed class LayoutFixture : ObservableObject
    {
        public bool IsBusy { get; private set; }
        public bool IsScanning => IsBusy;
        public bool IsSearchPending { get; private set; }
        public bool IsPreservingSelection { get; private set; }
        public bool IsShowingSelectedOnly { get; private set; }
        public bool HasColumnFilters { get; set; }
        public bool HasSelection { get; set; }
        public string SelectionSummary => "3 selected";
        public object SelectedOutputFormat { get; } = new { DisplayName = "Full paths" };
        public bool CanShowSelectedOnly => true;
        public bool CanTypeSearch => true;
        public bool CanEditWorkspace => true;
        public bool HasPublishedResults => true;
        public bool ShowCancelAction => IsBusy || IsSearchPending || IsPreservingSelection;
        public bool IsDetailsOpen => false;
        public bool HasLoadedItems => false;
        public bool IsCatalogEmptyVisible => false;
        public bool IsFilteredEmpty => false;
        public bool CanTestEmptyProfile => false;
        public bool IsResultInteractionEnabled => true;
        public string FilterText { get; set; } = string.Empty;
        public string ScanActionText => "Refresh results";
        public string RootPath => @"\\files.example.test\Team archives\Engineering\Projects\2026\Very long location name";
        public string FilterSummary => "100,000 / 1,000,000";
        public string ProgressMessage => "100,000 folders examined — " + new string('W', 500);
        public string BusyProgressCaption => "Previous results · refreshing · " + ProgressMessage;
        public string ScanDisplayNotice => "The full scan notification remains available in the tooltip. " + new string('W', 200);
        public string SelectedOnlyDescription => "Showing every checked item, including checks outside the current search. " + new string('W', 200);
        public string ResultProvenance => "Saved results · 2026-10-16 14:32";
        public string ResultSourceDescription => "A complete, accessible source description.";
        public bool HasCompletedScanElapsed => true;
        public string LastCompletedScanElapsedText => "Scan duration 12:34:56";
        public object[] Profiles { get; } = [new { Id = "qa-long-profile", DisplayName = "An intentionally long structured profile name for narrow-window layout verification" }];
        public object? SelectedProfile { get; set; }
        public object WorkspaceTransfer { get; } = new
        {
            IsBusy = false, IsImported = false, SourceDescription = "This computer",
            SelectedSource = new { DisplayName = "This computer" },
        };
        public object[] ActiveColumnFilters { get; } = Enumerable.Range(0, 8).Select(index => (object)new
        {
            Description = $"Column {index + 1}: contains a deliberately long filter value",
            RemoveCommand = new RelayCommand(static () => { }),
        }).ToArray();
        public IRelayCommand ImportProfileCommand { get; } = new RelayCommand(static () => { });
        public IRelayCommand ClearColumnFiltersCommand { get; } = new RelayCommand(static () => { });
        public LayoutFixture() => SelectedProfile = Profiles[0];
        public void SetState(bool busy, bool pending, bool preserving, bool review)
        {
            IsBusy = busy; IsSearchPending = pending; IsPreservingSelection = preserving; IsShowingSelectedOnly = review;
            Refresh();
        }
        public void Refresh() => OnPropertyChanged(string.Empty);
    }
}
