using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using FindEverything.Application;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Desktop;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class DesktopCompositionSmokeTests
{
    [Fact]
    public async Task Shell_pages_and_navigation_resolve_on_an_sta_thread()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunSmokeTest(completion))
        {
            IsBackground = true,
            Name = "FindEverything WPF smoke test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(exception);
    }

    private static void RunSmokeTest(TaskCompletionSource<Exception?> completion)
    {
        App? application = null;
        MainWindow? window = null;
        Exception? failure = null;
        try
        {
            application = new App();
            application.InitializeComponent();

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Appearance:Theme"] = "System",
                ["Appearance:Backdrop"] = "Auto",
            });
            var services = new ServiceCollection();
            var blockingCatalogService = new BlockingCatalogService();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services
                .AddFindEverythingApplication(configuration)
                .AddFindEverythingInfrastructure()
                .AddProfileRuntime()
                .AddDesktopPresentation(
                    configuration,
                    new AppPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "findeverything-smoke.json")));
            services.AddSingleton(blockingCatalogService);
            services.AddSingleton<ICatalogService>(
                static provider => provider.GetRequiredService<BlockingCatalogService>());

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            window = provider.GetRequiredService<MainWindow>();
            var filesPage = provider.GetRequiredService<FilesPage>();
            Assert.NotNull(filesPage);
            var catalogPage = provider.GetRequiredService<CatalogPage>();
            Assert.NotNull(catalogPage);
            var profilesPage = provider.GetRequiredService<ProfilesPage>();
            Assert.NotNull(profilesPage);
            var settingsPage = provider.GetRequiredService<SettingsPage>();
            Assert.NotNull(settingsPage);

            window.Height = window.MinHeight;
            application.MainWindow = window;
            window.Show();
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);

            // FluentWindow used to replace this Freezable whenever the backdrop
            // changed. Keep the same WindowChrome even on hosts where Auto itself
            // is not supported, so this exercises the WindowsBase failure path.
            window.WindowBackdropType = WindowBackdropType.Auto;
            Assert.Same(chrome, WindowChrome.GetWindowChrome(window));
            window.WindowBackdropType = WindowBackdropType.None;
            Assert.Same(chrome, WindowChrome.GetWindowChrome(window));

            var appearance = provider.GetRequiredService<IAppearanceService>();
            appearance.Apply(
                window,
                new AppearanceOptions
                {
                    Theme = ThemePreference.Light,
                    Backdrop = BackdropPreference.None,
                });
            var navigation = provider.GetRequiredService<INavigationService>();
            Assert.True(navigation.Navigate(typeof(FilesPage)));
            VerifyPageScrollContracts(filesPage, catalogPage);
            Assert.True(navigation.Navigate(typeof(CatalogPage)));
            Assert.True(navigation.Navigate(typeof(ProfilesPage)));
            VerifyProfilesScrolling(window, profilesPage);
            Assert.True(navigation.Navigate(typeof(SettingsPage)));
            VerifySettingsLayout(window, settingsPage);
            VerifyScanConsole(provider, window);
            VerifyDynamicGridHighlighting();
            window.Close();
            window = null;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                window?.Close();
                application?.Shutdown();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            completion.TrySetResult(failure);
        }
    }

    private static void VerifyScanConsole(IServiceProvider provider, MainWindow mainWindow)
    {
        var panelController = provider.GetRequiredService<IScanConsolePanelController>();
        var sink = provider.GetRequiredService<ICatalogScanTraceSink>();
        var viewModel = provider.GetRequiredService<ScanConsoleViewModel>();
        Assert.Same(viewModel, sink);
        Assert.Same(viewModel, panelController);

        var panel = Assert.IsType<ScanConsolePanel>(
            mainWindow.FindName("ScanConsolePanel"));
        var toggleButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            mainWindow.FindName("ScanConsoleToggleButton"));
        Assert.Equal(Visibility.Collapsed, panel.Visibility);

        panelController.Show();
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);
        var traceList = Assert.IsType<ListBox>(panel.FindName("TraceList"));
        Assert.True(VirtualizingPanel.GetIsVirtualizing(traceList));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(traceList));
        Assert.Single(System.Windows.Application.Current.Windows.OfType<MainWindow>());

        var operationId = Guid.NewGuid();
        sink.Report(new CatalogScanTraceEvent(
            operationId,
            1,
            DateTimeOffset.UtcNow,
            CatalogScanTraceKind.Started,
            "test-profile",
            "테스트 프로필",
            @"C:\Root",
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, object?>(),
            [],
            null,
            "시작"));
        sink.Report(new CatalogScanTraceEvent(
            operationId,
            2,
            DateTimeOffset.UtcNow,
            CatalogScanTraceKind.DirectoryVisited,
            "test-profile",
            "테스트 프로필",
            @"C:\Root",
            @"C:\Root\2026\0521_Project",
            @"2026\0521_Project",
            @"C:\Root\2026\0521_Project",
            ProfileMapStatus.Success,
            "project-rule",
            new Dictionary<string, object?> { ["date"] = new DateTime(2026, 5, 21) },
            [],
            DirectoryTraversalDecision.SkipDescendants,
            "일치"));
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(150));

        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Contains("[MATCH] [PRUNE]", viewModel.Lines[1].Text, StringComparison.Ordinal);
        Assert.Contains("input=\"C:\\Root\\2026\\0521_Project\"", viewModel.Lines[1].Text, StringComparison.Ordinal);
        Assert.Equal(1, viewModel.VisitedCount);
        Assert.Equal(1, viewModel.MatchedCount);
        Assert.Equal(1, viewModel.PrunedCount);

        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.Equal(2, viewModel.Lines.Count);

        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);

        var catalog = provider.GetRequiredService<CatalogViewModel>();
        var badge = Assert.IsType<InfoBadge>(mainWindow.FindName("CatalogScanBadge"));
        var navigationItem = Assert.IsType<NavigationViewItem>(
            mainWindow.FindName("CatalogNavigationItem"));
        Assert.Equal(typeof(CatalogPage), navigationItem.TargetPageType);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        catalog.IsScanning = true;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        badge.ApplyTemplate();
        Assert.Contains(FindVisualChildren<ProgressRing>(badge), static ring => ring.IsIndeterminate);
        catalog.IsScanning = false;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);

        viewModel.ClosePanelCommand.Execute(null);
        var profile = new StubLoadedProfile();
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));
        catalog.RootPath = Path.GetTempPath();
        var scanTask = catalog.ScanCommand.ExecuteAsync(null);

        Assert.True(catalog.IsScanning);
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => provider.GetRequiredService<BlockingCatalogService>().HasStarted,
            TimeSpan.FromSeconds(3));

        provider.GetRequiredService<BlockingCatalogService>().Complete(profile.Descriptor);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => scanTask.IsCompleted,
            TimeSpan.FromSeconds(3));
        scanTask.GetAwaiter().GetResult();
        Assert.False(catalog.IsScanning);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
    }

    private static void PumpDispatcherFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = duration,
        };
        timer.Tick += OnTick;
        timer.Start();
        Dispatcher.PushFrame(frame);

        void OnTick(object? sender, EventArgs eventArgs)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            frame.Continue = false;
        }
    }

    private static void PumpDispatcherUntil(
        Dispatcher dispatcher,
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.True(condition(), $"조건이 {timeout.TotalSeconds:0.#}초 안에 충족되지 않았습니다.");
    }

    private static void VerifyProfilesScrolling(Window window, ProfilesPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));

        var tabControl = Assert.IsType<TabControl>(page.FindName("ProfilesTabControl"));
        Assert.Equal(HorizontalAlignment.Stretch, tabControl.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Stretch, tabControl.VerticalContentAlignment);
        var viewModel = Assert.IsType<ProfilesViewModel>(page.DataContext);
        Assert.True(viewModel.IsGuidedMode);
        Assert.NotEmpty(viewModel.GeneratedPatternPreview);
        var templateBox = Assert.IsType<Wpf.Ui.Controls.TextBox>(
            page.FindName("ProfilePathTemplateTextBox"));
        Assert.True(templateBox.IsReadOnly);
        Assert.IsType<Border>(page.FindName("AssignmentPickerPanel"));
        Assert.NotEmpty(viewModel.GuidedPathSegments);
        VerifyGuidedPathButtonAuthoring(viewModel);

        PumpLayout(window, page);
        VerifyProfilesFluentControls(page);
        var editorScrollViewer = Assert.IsType<ScrollViewer>(
            page.FindName("ProfileEditorScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), editorScrollViewer.Padding);
        VerifyScrollable(window, page, editorScrollViewer);

        tabControl.SelectedIndex = 1;
        PumpLayout(window, page);
        var loadStatusScrollViewer = Assert.IsType<ScrollViewer>(
            page.FindName("ProfileLoadStatusScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), loadStatusScrollViewer.Padding);
        VerifyScrollable(window, page, loadStatusScrollViewer);

        tabControl.SelectedIndex = 0;
        PumpLayout(window, page);
    }

    private static void VerifyProfilesFluentControls(ProfilesPage page)
    {
        foreach (var resourceKey in new[]
                 {
                     "ActionableEditorInfoBarStyle",
                     "IconOnlyButtonStyle",
                     "GuidedModeChoiceStyle",
                 })
        {
            var style = Assert.IsType<Style>(page.Resources[resourceKey]);
            Assert.NotNull(style.BasedOn);
        }

        var modeButtons = FindVisualChildren<RadioButton>(page)
            .Where(static button =>
                System.Windows.Automation.AutomationProperties.GetName(button) is
                    "초보자 모드" or "전문가 모드")
            .ToArray();
        Assert.Equal(2, modeButtons.Length);
        Assert.All(modeButtons, static button =>
            Assert.Equal("ProfileEditorMode", button.GroupName));
    }

    private static void VerifyGuidedPathButtonAuthoring(ProfilesViewModel viewModel)
    {
        var nameField = Assert.Single(viewModel.DraftFields);
        viewModel.ClearFieldAssignmentsCommand.Execute(nameField);
        viewModel.SamplePath = @"C:\Archive\2026\0521_Project-A";
        viewModel.BuildTemplateFromSampleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(4, viewModel.GuidedPathSegments.Count);

        viewModel.BeginAssignValueCommand.Execute(nameField);
        viewModel.AssignPathChoiceCommand.Execute(viewModel.GuidedPathSegments[1].WholeChoice);

        viewModel.AddFieldCommand.Execute(null);
        var dateField = viewModel.DraftFields[1];
        dateField.Header = "기준일";
        dateField.Kind = ProfileFieldValueKind.DateTime;
        dateField.DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay;
        viewModel.BeginAssignYearCommand.Execute(dateField);
        viewModel.AssignPathChoiceCommand.Execute(viewModel.GuidedPathSegments[2].WholeChoice);
        var monthDay = Assert.Single(
            viewModel.GuidedPathSegments[3].PartChoices,
            static choice => choice.Value == "0521");
        viewModel.AssignPathChoiceCommand.Execute(monthDay);

        Assert.Equal(
            "C:/{name@name}/{field-1@field_1Year}/{field-1@field_1MonthDay}_Project-A",
            viewModel.DraftPathTemplate);
        Assert.NotEmpty(viewModel.GeneratedPatternPreview);
        Assert.Contains("Project-A", viewModel.DraftPathTemplate);

        viewModel.SelectTerminalFieldCommand.Execute(dateField);
        viewModel.UseExpertModeCommand.Execute(null);
        Assert.True(viewModel.IsExpertMode);
        var rule = Assert.Single(viewModel.DraftRules);
        Assert.Equal(
            "field_1Year, field_1MonthDay",
            rule.StopTraversalGroupsText);

        rule.StopTraversalGroupsText = "year, arbitrary";
        Assert.False(viewModel.CanUseGuidedMode);
        rule.StopTraversalGroupsText = "field_1Year, field_1MonthDay";
        Assert.True(viewModel.CanUseGuidedMode);

        dateField.GroupName = "legacyDate";
        viewModel.ValidateDraftCommand.Execute(null);
        Assert.Contains("동시에", viewModel.EditorStatusMessage);
        dateField.GroupName = string.Empty;
        Assert.True(viewModel.CanUseGuidedMode);

        viewModel.UseGuidedModeCommand.Execute(null);
        Assert.True(viewModel.IsGuidedMode);
        Assert.True(dateField.IsTerminalField);

        viewModel.AddFieldCommand.Execute(null);
        var removedWhilePicking = viewModel.DraftFields[^1];
        viewModel.BeginAssignValueCommand.Execute(removedWhilePicking);
        Assert.True(viewModel.IsAssignmentPickerOpen);
        viewModel.RemoveFieldCommand.Execute(removedWhilePicking);
        Assert.False(viewModel.IsAssignmentPickerOpen);

        viewModel.AddFieldCommand.Execute(null);
        var changedWhilePicking = viewModel.DraftFields[^1];
        changedWhilePicking.Kind = ProfileFieldValueKind.DateTime;
        changedWhilePicking.DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay;
        viewModel.BeginAssignMonthDayCommand.Execute(changedWhilePicking);
        Assert.True(viewModel.IsAssignmentPickerOpen);
        changedWhilePicking.DateSourcePreset = GuidedDateSourcePreset.SingleValue;
        Assert.False(viewModel.IsAssignmentPickerOpen);
        viewModel.RemoveFieldCommand.Execute(changedWhilePicking);
    }

    private static void VerifySettingsLayout(Window window, SettingsPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));
        PumpLayout(window, page);

        var scrollViewer = Assert.IsType<ScrollViewer>(page.FindName("SettingsScrollViewer"));
        Assert.Equal(HorizontalAlignment.Stretch, scrollViewer.HorizontalContentAlignment);
        Assert.Equal(new Thickness(0, 0, 16, 0), scrollViewer.Padding);
        Assert.True(scrollViewer.ActualWidth > 0);

        var inputs = FindVisualChildren<FrameworkElement>(scrollViewer)
            .Where(static element => element is System.Windows.Controls.TextBox or ComboBox)
            .ToArray();
        Assert.NotEmpty(inputs);
        foreach (var input in inputs)
        {
            Assert.Equal(double.PositiveInfinity, input.MaxWidth);
            Assert.Equal(HorizontalAlignment.Stretch, input.HorizontalAlignment);
        }
    }

    private static void VerifyPageScrollContracts(FilesPage filesPage, CatalogPage catalogPage)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(filesPage));
        Assert.False(ScrollViewer.GetCanContentScroll(catalogPage));
        var filterScrollViewer = Assert.IsType<ScrollViewer>(
            filesPage.FindName("FileFilterScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), filterScrollViewer.Padding);
        Assert.Equal(ScrollBarVisibility.Disabled, filterScrollViewer.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, filterScrollViewer.VerticalScrollBarVisibility);
    }

    private static void VerifyScrollable(
        Window window,
        FrameworkElement page,
        ScrollViewer scrollViewer)
    {
        Assert.True(scrollViewer.ActualHeight > 0);
        Assert.True(scrollViewer.ExtentHeight > scrollViewer.ViewportHeight);
        Assert.True(scrollViewer.ScrollableHeight > 0);
        Assert.Equal(Visibility.Visible, scrollViewer.ComputedVerticalScrollBarVisibility);

        scrollViewer.ScrollToEnd();
        PumpLayout(window, page);
        Assert.True(scrollViewer.VerticalOffset > 0);
        Assert.InRange(
            Math.Abs(scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset),
            0d,
            1d);

        scrollViewer.ScrollToHome();
        PumpLayout(window, page);
    }

    private static void PumpLayout(Window window, FrameworkElement element)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        element.UpdateLayout();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        element.UpdateLayout();
    }

    private static void VerifyDynamicGridHighlighting()
    {
        var field = new ProfileFieldDescriptor(
            "client",
            "client",
            "고객",
            0,
            Required: true,
            ProfileFieldValueKind.String,
            IsNullable: false,
            ParseFormat: null,
            DisplayFormat: null);
        var item = new CatalogItemViewModel(
            new CatalogItem(
                @"C:\Archive\Apollo",
                "Apollo",
                "sample",
                new object(),
                new Dictionary<string, object?> { ["client"] = "Apollo" },
                CoveragePending: false),
            [field]);
        var grid = new System.Windows.Controls.DataGrid
        {
            AutoGenerateColumns = false,
            EnableColumnVirtualization = false,
            EnableRowVirtualization = false,
            IsReadOnly = true,
            ItemsSource = new[] { item },
            DataContext = new FilterContext(string.Empty),
        };
        DynamicProfileGrid.SetFields(grid, new[] { field });

        var host = new Window
        {
            Width = 800,
            Height = 240,
            Content = grid,
            Opacity = 0,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        try
        {
            host.Show();
            grid.UpdateLayout();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            var textBlock = Assert.Single(
                FindVisualChildren<System.Windows.Controls.TextBlock>(grid),
                block => TextHighlighting.GetDisplayText(block) == "Apollo");
            Assert.Equal("Apollo", string.Concat(
                textBlock.Inlines.OfType<Run>().Select(static run => run.Text)));

            grid.DataContext = new FilterContext("  POL  ");
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            var runs = textBlock.Inlines.OfType<Run>().ToArray();
            Assert.Equal("Apollo", string.Concat(runs.Select(static run => run.Text)));
            Assert.Single(runs, static run => run.Background is not null);
            Assert.Contains(runs, static run => run.Text == "pol" && run.Background is not null);
        }
        finally
        {
            host.Close();
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class BlockingCatalogService : ICatalogService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CatalogResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasStarted => _started.Task.IsCompleted;

        public async Task<CatalogResult> DiscoverAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public Task<CatalogResult> LoadExistingAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CatalogResult> ScanAndLoadAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Complete(ProfileDescriptor descriptor) =>
            _completion.TrySetResult(new CatalogResult(
                descriptor,
                [],
                [],
                0,
                0,
                false,
                null));
    }

    private sealed class StubLoadedProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(
            "scan-smoke-profile",
            "1.0.0",
            "스캔 스모크 프로필",
            ProfileCandidateKind.Directory,
            [],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            ProfileMapResult.NoMatch();
    }

    private sealed record FilterContext(string FilterText);
}
