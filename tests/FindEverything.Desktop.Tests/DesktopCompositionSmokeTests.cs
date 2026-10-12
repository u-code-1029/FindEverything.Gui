using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using FindEverything.Infrastructure.Persistence;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class DesktopCompositionSmokeTests
{
    [Fact]
    public void Shipped_configuration_uses_the_runtime_profile_contract()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var options = new PluginDiscoveryOptions();
        configuration.GetSection(PluginDiscoveryOptions.SectionName).Bind(options);

        Assert.Equal(ProfileContract.CurrentMajor, options.ContractMajor);
    }

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
        System.Windows.Application? application = null;
        MainWindow? window = null;
        Exception? failure = null;
        using var dispatcherContext = UseDispatcherSynchronizationContext(
            Dispatcher.CurrentDispatcher);
        try
        {
            application = CreateTestApplication();

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Appearance:Theme"] = "System",
                ["Appearance:Backdrop"] = "Auto",
                ["Localization:CultureName"] = LocalizationOptions.KoreanCultureName,
            });
            var services = new ServiceCollection();
            var blockingCatalogService = new BlockingCatalogService();
            var completionNotifier = new RecordingScanCompletionNotifier();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services
                .AddFindEverythingApplication(configuration)
                .AddFindEverythingInfrastructure()
                .AddProfileRuntime()
                .AddDesktopPresentation(
                    configuration,
                    new AppPaths(
                        Path.Combine(Path.GetTempPath(), $"findeverything-smoke-{Guid.NewGuid():N}"),
                        Path.Combine(Path.GetTempPath(), $"findeverything-smoke-settings-{Guid.NewGuid():N}.json")));
            services.AddSingleton(blockingCatalogService);
            services.AddSingleton<ICatalogService>(
                static provider => provider.GetRequiredService<BlockingCatalogService>());
            services.AddSingleton(completionNotifier);
            services.AddSingleton<IScanCompletionNotifier>(completionNotifier);
            services.AddSingleton<IDraftDecisionPrompt, DiscardDraftPrompt>();
            // Keep the existing live-scan/diagnostic regressions on their original
            // fake service. A separate helper exercises the real projection path.
            services.RemoveAll<ICatalogProjectionStore>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            window = provider.GetRequiredService<MainWindow>();
            var homePage = provider.GetRequiredService<HomePage>();
            Assert.NotNull(homePage);
            var filesPage = provider.GetRequiredService<FilesPage>();
            Assert.NotNull(filesPage);
            var catalogPage = provider.GetRequiredService<CatalogPage>();
            Assert.NotNull(catalogPage);
            var profilesPage = provider.GetRequiredService<ProfilesPage>();
            Assert.NotNull(profilesPage);
            var profilePlaygroundPage = provider.GetRequiredService<ProfilePlaygroundPage>();
            Assert.NotNull(profilePlaygroundPage);
            var outputFormatsPage = provider.GetRequiredService<OutputFormatsPage>();
            Assert.NotNull(outputFormatsPage);
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
            CaptureOptionalQaScreenshots(provider, window, filesPage, catalogPage,
                profilesPage, profilePlaygroundPage, outputFormatsPage, settingsPage);
            Assert.True(navigation.Navigate(typeof(HomePage)));
            Assert.NotNull(homePage.FindName("HomeScrollViewer"));
            Assert.NotNull(homePage.FindName("FilesActionCard"));
            Assert.NotNull(homePage.FindName("CatalogActionCard"));
            Assert.True(navigation.Navigate(typeof(FilesPage)));
            VerifyPageScrollContracts(filesPage, catalogPage);
            VerifyCompactSelectionPages(filesPage, catalogPage);
            VerifyCoreWorkspaceBounds(filesPage, catalogPage);
            Assert.True(navigation.Navigate(typeof(CatalogPage)));
            Assert.True(navigation.Navigate(typeof(ProfilesPage)));
            VerifyProfilesScrolling(window, profilesPage);
            Assert.True(navigation.Navigate(typeof(ProfilePlaygroundPage)));
            VerifyProfilePlaygroundLayout(window, profilePlaygroundPage);
            Assert.True(navigation.Navigate(typeof(OutputFormatsPage)));
            Assert.NotNull(outputFormatsPage.FindName("OutputFormatsScrollViewer"));
            Assert.True(navigation.Navigate(typeof(SettingsPage)));
            VerifySettingsLayout(window, settingsPage);
            VerifyScanConsole(provider, window, catalogPage);
            VerifyProjectionQueries(provider, window);
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

    private static System.Windows.Application CreateTestApplication()
    {
        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        application.Resources.MergedDictionaries.Add(
            new ThemesDictionary { Theme = ApplicationTheme.Light });
        application.Resources.MergedDictionaries.Add(new ControlsDictionary());
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/FindEverything.Gui;component/Themes/DesignTokens.xaml",
                UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/FindEverything.Gui;component/Themes/ComponentStyles.xaml",
                UriKind.Relative),
        });
        application.Resources["BooleanToVisibilityConverter"] =
            new BooleanToVisibilityConverter();
        LocalizationBootstrapper.Apply(
            application,
            new LocalizationOptions
            {
                CultureName = LocalizationOptions.KoreanCultureName,
            });
        return application;
    }

    private static void VerifyCompactSelectionPages(
        FilesPage filesPage,
        CatalogPage catalogPage)
    {
        var filesViewModel = Assert.IsType<FileSearchViewModel>(filesPage.DataContext);
        var catalogViewModel = Assert.IsType<CatalogViewModel>(catalogPage.DataContext);
        Assert.False(filesViewModel.IsDetailsOpen);
        Assert.False(catalogViewModel.IsDetailsOpen);

        var filesGrid = Assert.IsType<System.Windows.Controls.DataGrid>(
            filesPage.FindName("ResultsGrid"));
        var catalogGrid = Assert.IsType<System.Windows.Controls.DataGrid>(
            catalogPage.FindName("ResultsGrid"));
        Assert.Equal(DataGridSelectionMode.Extended, filesGrid.SelectionMode);
        Assert.Equal(DataGridSelectionMode.Extended, catalogGrid.SelectionMode);
        Assert.NotNull(filesGrid.ContextMenu);
        Assert.NotNull(catalogGrid.ContextMenu);
        Assert.IsType<Wpf.Ui.Controls.TextBox>(
            filesPage.FindName("PartialRefreshPathTextBox"));
        var partialRefreshButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            filesPage.FindName("PartialRefreshButton"));
        Assert.NotNull(partialRefreshButton.Command);

        var filesToolbar = Assert.IsType<Border>(filesPage.FindName("SelectionToolbar"));
        var catalogToolbar = Assert.IsType<Border>(catalogPage.FindName("SelectionToolbar"));
        Assert.Equal(Visibility.Collapsed, filesToolbar.Visibility);
        Assert.Equal(Visibility.Collapsed, catalogToolbar.Visibility);
        var exportButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            catalogPage.FindName("ExportSelectionButton"));
        Assert.NotNull(exportButton.Command);
    }

    private static void VerifyCoreWorkspaceBounds(FilesPage filesPage, CatalogPage catalogPage)
    {
        foreach (var page in new Page[] { filesPage, catalogPage })
        {
            var originalContext = page.DataContext;
            try
            {
                page.DataContext = new WorkspaceLayoutFixture(page is CatalogPage);
                foreach (var (size, minimumRatio) in new[]
                         {
                             (new Size(1140, 790), 0.70),
                             (new Size(660, 600), 0.60),
                         })
                {
                    page.Measure(size);
                    page.Arrange(new Rect(size));
                    page.UpdateLayout();
                    SaveQaImage(page, $"{page.GetType().Name}-{(size.Width >= 900 ? "wide" : "small")}-workspace.png");
                    page.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
                    page.UpdateLayout();
                    var grid = Assert.IsType<System.Windows.Controls.DataGrid>(page.FindName("ResultsGrid"));
                    var toolbar = Assert.IsType<Border>(page.FindName("SelectionToolbar"));
                    Assert.True(grid.ActualHeight / page.ActualHeight >= minimumRatio,
                        $"{page.GetType().Name}: results occupy {grid.ActualHeight / page.ActualHeight:P1} at {size}.");
                    Assert.True(toolbar.ActualHeight > 0);
                    var gridBottom = grid.TranslatePoint(new Point(0, grid.ActualHeight), page).Y;
                    var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), page).Y;
                    Assert.True(toolbarTop >= gridBottom, "Selection actions must not cover the last row or scrollbar.");
                    var header = grid.Columns[0].Header as Wpf.Ui.Controls.Button;
                    Assert.NotNull(header);
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(header)));
                    Assert.False(ResultGridInput.IsInteractiveOrigin(new System.Windows.Controls.TextBlock()));
                    Assert.True(ResultGridInput.IsInteractiveOrigin(new System.Windows.Controls.CheckBox()));
                    Assert.True(ResultGridInput.IsInteractiveOrigin(new Wpf.Ui.Controls.TextBox()));
                }
            }
            finally
            {
                page.DataContext = originalContext;
                page.InvalidateMeasure();
            }
        }
    }

    private static void CaptureOptionalQaScreenshots(IServiceProvider provider, MainWindow window,
        FilesPage files, CatalogPage catalog, ProfilesPage profiles, ProfilePlaygroundPage playground,
        OutputFormatsPage outputFormats, SettingsPage settings)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR"))) return;
        var navigation = provider.GetRequiredService<INavigationService>();
        var appearance = provider.GetRequiredService<IAppearanceService>();
        var width = window.Width;
        var height = window.Height;
        window.Width = 1360;
        window.Height = 860;
        try
        {
            Capture(files, new WorkspaceLayoutFixture(isCatalog: false), "files-light.png");
            Capture(catalog, new WorkspaceLayoutFixture(isCatalog: true), "catalog-light.png");
            window.Width = 760;
            window.Height = 640;
            Capture(catalog, new WorkspaceLayoutFixture(isCatalog: true), "catalog-small.png");
            window.Width = 1360;
            window.Height = 860;
            appearance.Apply(window, new AppearanceOptions { Theme = ThemePreference.Dark, Backdrop = BackdropPreference.None });
            Capture(catalog, new WorkspaceLayoutFixture(isCatalog: true), "catalog-dark.png");
            appearance.Apply(window, new AppearanceOptions { Theme = ThemePreference.Light, Backdrop = BackdropPreference.None });

            var editor = Assert.IsType<ProfilesViewModel>(profiles.DataContext);
            var previousStep = editor.CurrentEditorStep;
            editor.GoToEditorStepCommand.Execute(ProfileEditorStep.PathAndFields);
            Capture(profiles, null, "profiles-light.png");
            editor.GoToEditorStepCommand.Execute(previousStep);

            var input = @"C:\Archive\Clients\Apollo\2026\0521_Project";
            var rules = new[] { new ProfileRegexRuleManifest
            {
                Id = "project-folders", Pattern = @".*[\\/](?<client>Apollo)[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<project>[^\\/]+)$",
                MatchMode = ProfileRegexMatchMode.Full, TimeoutMilliseconds = 100,
            } };
            Capture(playground, new PlaygroundLayoutFixture(input, ProfileRegexDebugBuilder.Build(input, rules)), "playground-light.png");
            Capture(outputFormats, null, "output-formats-light.png");
            Capture(settings, null, "settings-light.png");
        }
        finally
        {
            appearance.Apply(window, new AppearanceOptions { Theme = ThemePreference.Light, Backdrop = BackdropPreference.None });
            window.Width = width;
            window.Height = height;
            navigation.Navigate(typeof(HomePage));
            PumpLayout(window, window);
        }

        void Capture(Page page, object? fixture, string fileName)
        {
            var original = page.DataContext;
            try
            {
                if (fixture is not null) page.DataContext = fixture;
                Assert.True(navigation.Navigate(page.GetType()));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(80));
                PumpLayout(window, page);
                if (fixture is WorkspaceLayoutFixture
                    && page.FindName("ResultsGrid") is System.Windows.Controls.DataGrid sampleGrid)
                {
                    sampleGrid.SelectAll();
                    PumpLayout(window, page);
                }
                SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content), fileName);
            }
            finally { page.DataContext = original; page.InvalidateMeasure(); }
        }
    }

    private static void SaveQaImage(FrameworkElement visual, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        visual.UpdateLayout();
        var width = Math.Max(1, checked((int)Math.Ceiling(visual.ActualWidth)));
        var height = Math.Max(1, checked((int)Math.Ceiling(visual.ActualHeight)));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = visual.TryFindResource("ApplicationBackgroundBrush") as Brush
            ?? visual.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush
            ?? new SolidColorBrush(fileName.Contains("-dark", StringComparison.OrdinalIgnoreCase)
                ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243));
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static void VerifyScanConsole(
        IServiceProvider provider,
        MainWindow mainWindow,
        CatalogPage catalogPage)
    {
        var panelController = provider.GetRequiredService<IScanConsolePanelController>();
        var sink = provider.GetRequiredService<ICatalogScanTraceSink>();
        var viewModel = provider.GetRequiredService<ScanConsoleViewModel>();
        Assert.Same(viewModel, sink);
        Assert.Same(viewModel, panelController);

        var panel = Assert.IsType<ScanConsolePanel>(
            catalogPage.FindName("ScanConsolePanel"));
        Assert.Null(mainWindow.FindName("ScanConsolePanel"));
        var pageLayout = Assert.IsType<Grid>(catalogPage.FindName("CatalogPageLayout"));
        var pageContent = Assert.IsAssignableFrom<Grid>(catalogPage.FindName("CatalogPageContent"));
        var resultsGrid = Assert.IsType<System.Windows.Controls.DataGrid>(
            catalogPage.FindName("ResultsGrid"));
        var toggleButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            mainWindow.FindName("ScanConsoleToggleButton"));
        var navigation = Assert.IsType<NavigationView>(mainWindow.FindName("RootNavigation"));
        var snackbarPresenter = Assert.IsType<SnackbarPresenter>(
            mainWindow.FindName("SnackbarPresenter"));
        var navigationService = provider.GetRequiredService<INavigationService>();

        // The panel is page-local, but the title-bar button is global. A click from
        // another page must navigate to the owning CatalogPage and reveal it instead
        // of toggling invisible state.
        Assert.True(navigationService.Navigate(typeof(FilesPage)));
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.False(viewModel.IsPanelOpen);
        Assert.NotNull(toggleButton.Command);
        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        mainWindow.UpdateLayout();
        Assert.Equal(
            typeof(CatalogPage),
            Assert.IsType<NavigationViewItem>(navigation.SelectedItem).TargetPageType);
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);

        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        mainWindow.UpdateLayout();
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.Equal(0, snackbarPresenter.Margin.Bottom);
        mainWindow.UpdateLayout();
        var navigationHeight = navigation.ActualHeight;
        var pageLayoutHeight = pageLayout.ActualHeight;
        var pageContentHeight = pageContent.ActualHeight;

        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        mainWindow.UpdateLayout();
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.Equal(0, navigation.FrameMargin.Bottom);
        Assert.Equal(0, navigation.Padding.Bottom);
        Assert.Equal(0, snackbarPresenter.Margin.Bottom);
        Assert.InRange(Math.Abs(navigationHeight - navigation.ActualHeight), 0d, 1d);
        Assert.InRange(Math.Abs(pageLayoutHeight - pageLayout.ActualHeight), 0d, 1d);
        Assert.True(pageContent.ActualHeight < pageContentHeight);
        Assert.InRange(panel.ActualHeight / pageLayout.ActualHeight, 0.25, 0.45);
        var traceList = Assert.IsType<ListBox>(panel.FindName("TraceList"));
        var saveLogButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            panel.FindName("SaveScanLogButton"));
        Assert.Same(viewModel.SaveLogCommand, saveLogButton.Command);
        Assert.Equal("로그 저장", saveLogButton.Content);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(traceList));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(traceList));
        var contextMenu = Assert.IsType<System.Windows.Controls.ContextMenu>(
            traceList.ContextMenu);
        Assert.Collection(
            contextMenu.Items.Cast<object>(),
            item => Assert.Equal(
                "메시지 전체 복사",
                Assert.IsType<System.Windows.Controls.MenuItem>(item).Header),
            item => Assert.Equal(
                "경로만 복사",
                Assert.IsType<System.Windows.Controls.MenuItem>(item).Header));
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
        Assert.Equal(@"C:\Root\2026\0521_Project", viewModel.Lines[1].Path);
        Assert.Equal(1, viewModel.VisitedCount);
        Assert.Equal(1, viewModel.MatchedCount);
        Assert.Equal(1, viewModel.PrunedCount);

        // Opening the real detached WPF ContextMenu must inherit the ListBox view
        // model through PlacementTarget and bind both copy commands.
        traceList.SelectedItem = viewModel.Lines[1];
        contextMenu.PlacementTarget = traceList;
        contextMenu.IsOpen = true;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        var menuItems = contextMenu.Items
            .Cast<System.Windows.Controls.MenuItem>()
            .ToArray();
        Assert.Same(viewModel.CopySelectedMessageCommand, menuItems[0].Command);
        Assert.Same(viewModel.CopySelectedPathCommand, menuItems[1].Command);
        Assert.True(menuItems[0].IsEnabled);
        Assert.True(menuItems[1].IsEnabled);
        contextMenu.IsOpen = false;

        toggleButton.Command.Execute(null);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.Equal(0, navigation.FrameMargin.Bottom);
        Assert.Equal(0, navigation.Padding.Bottom);
        Assert.Equal(0, snackbarPresenter.Margin.Bottom);
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
        navigation.IsPaneOpen = false;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(40, badge.Width);
        Assert.Equal(40, badge.Height);
        var compactRing = Assert.Single(FindVisualChildren<ProgressRing>(badge));
        Assert.Equal(HorizontalAlignment.Center, compactRing.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, compactRing.VerticalAlignment);
        navigation.IsPaneOpen = true;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(16, badge.Width);
        Assert.Equal(16, badge.Height);
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
        Assert.False(catalog.CanEditWorkspace);
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => provider.GetRequiredService<BlockingCatalogService>().HasStarted,
            TimeSpan.FromSeconds(3));

        catalog.FilterText = "Apollo";
        var matchingItem = new CatalogItem(
            Path.Combine(Path.GetTempPath(), "live-result-1"),
            "live-result-1",
            "live-rule",
            new object(),
            new Dictionary<string, object?> { ["client"] = "Apollo" },
            false);
        var filteredItem = new CatalogItem(
            Path.Combine(Path.GetTempPath(), "live-result-2"),
            "live-result-2",
            "live-rule",
            new object(),
            new Dictionary<string, object?> { ["client"] = "Fabrikam" },
            false);
        var blockingService = provider.GetRequiredService<BlockingCatalogService>();
        Task.Run(() => blockingService.ReportMatches(matchingItem, filteredItem))
            .GetAwaiter()
            .GetResult();
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => catalog.HasLoadedItems && catalog.Items.Count == 1,
            TimeSpan.FromSeconds(3));

        Assert.False(scanTask.IsCompleted);
        Assert.True(catalog.IsScanning);
        Assert.Equal("1 / 2개 항목", catalog.FilterSummary);
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Visible, resultsGrid.Visibility);
        Assert.Single(resultsGrid.Items.Cast<object>());
        Assert.Equal(
            "Apollo",
            Assert.IsType<CatalogItemViewModel>(catalog.Items.GetItemAt(0))
                .DisplayValues["client"]);
        catalog.FilterText = string.Empty;
        mainWindow.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal("2개 항목", catalog.FilterSummary);

        var atomicBatchItem = new CatalogItem(
            Path.Combine(Path.GetTempPath(), "live-batch-valid"),
            "live-batch-valid",
            "live-rule",
            new object(),
            new Dictionary<string, object?> { ["client"] = "Batch Valid" },
            false);
        var malformedBatchItem = new CatalogItem(
            Path.Combine(Path.GetTempPath(), "live-batch-malformed"),
            "live-batch-malformed",
            "live-rule",
            new object(),
            new Dictionary<string, object?> { ["client"] = new ThrowingFormattable() },
            false);
        Task.Run(() => blockingService.ReportMatches(atomicBatchItem, malformedBatchItem))
            .GetAwaiter()
            .GetResult();
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => catalog.ProgressMessage.StartsWith(
                "실시간 표시를 일시 중단했습니다.",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        // A failed conversion in the second item must not leave the first item
        // from the same live batch partially inserted into the backing list.
        Assert.Equal(2, catalog.Items.Count);
        Assert.DoesNotContain(
            catalog.Items.Cast<CatalogItemViewModel>(),
            item => item.FullPath == atomicBatchItem.FullPath);

        var reconciledItem = matchingItem with
        {
            Values = new Dictionary<string, object?> { ["client"] = "Apollo Final" },
        };
        blockingService.Complete(profile.Descriptor, reconciledItem, filteredItem);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => scanTask.IsCompleted,
            TimeSpan.FromSeconds(3));
        scanTask.GetAwaiter().GetResult();
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.False(catalog.IsScanning);
        Assert.True(catalog.CanEditWorkspace);
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal(2, resultsGrid.Items.Count);
        var reconciledViewModel = Assert.IsType<CatalogItemViewModel>(
            catalog.Items.GetItemAt(0));
        Assert.Equal("live-result-1", reconciledViewModel.RelativePath);
        Assert.Equal("Apollo Final", reconciledViewModel.DisplayValues["client"]);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        Assert.Contains("소요 1분 05.43초", catalog.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("이름 규칙 제외 2", catalog.StatusMessage, StringComparison.Ordinal);
        var notification = Assert.Single(
            provider.GetRequiredService<RecordingScanCompletionNotifier>().Notices);
        Assert.Equal("스캔 및 DB 저장 완료", notification.Title);
        Assert.Contains("소요 1분 05.43초", notification.Message, StringComparison.Ordinal);

        // The default scan is also the persistent workflow. It must keep the log
        // collapsed and may replace an automatic existing-index load atomically.
        viewModel.ClosePanelCommand.Execute(null);
        var persistentRoot = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(persistentRoot);
        catalog.RootPath = persistentRoot;
        catalog.DatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-{Guid.NewGuid():N}.db");
        var workspaceContext = provider.GetRequiredService<IWorkspaceContext>();
        var baselineWorkspaceCommit = catalog.CommitWorkspaceCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => baselineWorkspaceCommit.IsCompleted,
            TimeSpan.FromSeconds(3));
        baselineWorkspaceCommit.GetAwaiter().GetResult();

        // A destination whose parent is a regular file fails directory creation.
        // The failed preflight must happen before this new workspace is published.
        var workspaceBeforeRejectedIndex = workspaceContext.Current;
        var blockedDatabaseParent = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-blocker-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(blockedDatabaseParent, "block directory creation");
            catalog.DatabasePath = Path.Combine(blockedDatabaseParent, "index.db");
            var failedDatabaseScan = catalog.ScanCommand.ExecuteAsync(null);
            PumpDispatcherUntil(
                mainWindow.Dispatcher,
                () => failedDatabaseScan.IsCompleted,
                TimeSpan.FromSeconds(3));
            failedDatabaseScan.GetAwaiter().GetResult();
            Assert.Equal(workspaceBeforeRejectedIndex, workspaceContext.Current);
            Assert.Equal(1, blockingService.ScanAndLoadCallCount);
        }
        finally
        {
            File.Delete(blockedDatabaseParent);
        }

        catalog.DatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-{Guid.NewGuid():N}.db");
        var operationCoordinator = provider.GetRequiredService<IApplicationOperationCoordinator>();
        using var loadStarted = new ManualResetEventSlim();
        var replacedLoad = operationCoordinator.RunAsync(
            ApplicationOperationKind.IndexLoad,
            async cancellationToken =>
            {
                loadStarted.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(3)));
        var replacementScanTask = catalog.ScanCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => replacementScanTask.IsCompleted,
            TimeSpan.FromSeconds(3));
        replacementScanTask.GetAwaiter().GetResult();
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(20));

        Assert.Equal(2, blockingService.ScanAndLoadCallCount);
        Assert.True(replacedLoad.IsCanceled);
        Assert.Equal(catalog.DatabasePath, blockingService.LastScanAndLoadRequest?.DatabasePath);
        Assert.Equal("스캔 및 DB 저장 완료", catalog.StatusTitle);
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(
            "스캔 및 DB 저장 완료",
            provider.GetRequiredService<RecordingScanCompletionNotifier>().Notices[^1].Title);
        var savedWorkspace = workspaceContext.Current;
        Assert.Equal(catalog.RootPath, savedWorkspace.RootPath);
        Assert.Equal(
            catalog.DatabasePath,
            savedWorkspace.ProfileDatabasePaths![profile.Descriptor.Id]);

        // A local root draft and an external file-search database edit merge
        // field-by-field. Catalog keeps its profile-specific database path.
        var editedRootPath = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-edited-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(editedRootPath);
        var editedDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-catalog-edited-{Guid.NewGuid():N}.db");
        catalog.RootPath = editedRootPath;
        workspaceContext.SaveAsync(
                workspaceContext.Current with { DatabasePath = editedDatabasePath })
            .GetAwaiter()
            .GetResult();
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => PathsEqual(workspaceContext.Current.RootPath, editedRootPath)
                && PathsEqual(workspaceContext.Current.DatabasePath, editedDatabasePath),
            TimeSpan.FromSeconds(3));
        Assert.Equal(editedRootPath, catalog.RootPath);
        Assert.Equal(
            savedWorkspace.ProfileDatabasePaths![profile.Descriptor.Id],
            catalog.DatabasePath);

        // Clearing the profile override restores its LocalAppData default without
        // changing the independent file-search database path.
        catalog.DatabasePath = string.Empty;
        var defaultDatabaseCommit = catalog.CommitWorkspaceCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => defaultDatabaseCommit.IsCompleted,
            TimeSpan.FromSeconds(3));
        defaultDatabaseCommit.GetAwaiter().GetResult();
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.Equal(editedDatabasePath, workspaceContext.Current.DatabasePath);
        Assert.False(workspaceContext.Current.ProfileDatabasePaths?.ContainsKey(
            profile.Descriptor.Id) ?? false);
        Assert.Equal(
            provider.GetRequiredService<IIndexDatabasePathResolver>()
                .ResolveProfileDatabasePath(profile.Descriptor.Id, workspaceContext.Current),
            catalog.DatabasePath);

        // Validation fails before a new live-result session starts. The previous
        // completed rows stay visible, but must not be described as partial rows
        // from the rejected operation.
        catalog.RootPath = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-missing-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(catalog.RootPath));
        var rejectedScan = catalog.ScanCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => rejectedScan.IsCompleted,
            TimeSpan.FromSeconds(3));
        rejectedScan.GetAwaiter().GetResult();
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(20));

        Assert.Equal("오류", catalog.StatusTitle);
        Assert.Equal("작업 실패", catalog.ProgressMessage);
        Assert.DoesNotContain("부분 결과", catalog.StatusMessage, StringComparison.Ordinal);
        Assert.True(catalog.HasLoadedItems);
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal(
            2,
            provider.GetRequiredService<RecordingScanCompletionNotifier>().Notices.Count);
    }

    private static void VerifyProjectionQueries(IServiceProvider provider, Window window)
    {
        var root = Path.Combine(Path.GetTempPath(), $"findeverything-projection-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "metadata.db");
        var profile = new QueryTestProfile();
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));
        var workspace = new QueryTestWorkspace(new WorkspaceSnapshot("query-test", root, database,
            new Dictionary<string, string> { ["query-test"] = database }));
        using var projections = new SqliteCatalogProjectionStore(Path.Combine(root, "results.db"));
        var service = new QueryTestCatalogService(profile, root);
        var clipboard = new RecordingClipboard();
        using var viewModel = new CatalogViewModel(service,
            provider.GetRequiredService<IProfileCatalog>(), workspace,
            provider.GetRequiredService<IIndexDatabasePathResolver>(),
            provider.GetRequiredService<IDesktopPickerService>(), provider.GetRequiredService<IPathLauncher>(),
            provider.GetRequiredService<IApplicationOperationCoordinator>(),
            provider.GetRequiredService<IScanCompletionNotifier>(), provider.GetRequiredService<ISnackbarService>(),
            provider.GetRequiredService<ISelectionOutputFormatStore>(),
            provider.GetRequiredService<ISelectionOutputFormatter>(),
            provider.GetRequiredService<ICatalogSelectionExporter>(), clipboard,
            provider.GetRequiredService<IAppLocalizer>(), provider.GetRequiredService<ILogger<CatalogViewModel>>(),
            projectionStore: projections);

        Complete(viewModel.ScanCommand.ExecuteAsync(null));
        Assert.True(viewModel.UsesQuerySelection);
        Assert.Equal(1200, viewModel.Items.Count);
        Assert.Equal(500, Enumerable.Range(0, 500).Count(index =>
            viewModel.Items.GetItemAt(index) is CatalogItemViewModel { IsPlaceholder: false }));
        viewModel.SelectAllCommand.Execute(null);
        Assert.Equal(1200, viewModel.SelectedCount);
        Complete(viewModel.CopySelectionCommand.ExecuteAsync(null));
        var copied = clipboard.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1200, copied.Length);
        Assert.EndsWith("Item-0000", copied[0], StringComparison.Ordinal);
        Assert.EndsWith("Item-1199", copied[^1], StringComparison.Ordinal);

        var previousView = viewModel.Items;
        viewModel.ApplySort("DisplayValues[client]", EntrySortDirection.Descending);
        PumpDispatcherUntil(window.Dispatcher, () => !ReferenceEquals(previousView, viewModel.Items), TimeSpan.FromSeconds(4));
        Assert.Equal(1200, viewModel.SelectedCount);
        Complete(viewModel.CopySelectionCommand.ExecuteAsync(null));
        copied = clipboard.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith("Item-1199", copied[0], StringComparison.Ordinal);

        viewModel.FilterText = "Item-0000";
        PumpDispatcherUntil(window.Dispatcher, () => viewModel.Items.Count == 1, TimeSpan.FromSeconds(4));
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.CopySelectionCommand.CanExecute(null));
        viewModel.FilterText = string.Empty;
        PumpDispatcherUntil(window.Dispatcher, () => viewModel.Items.Count == 1200, TimeSpan.FromSeconds(4));

        Complete(viewModel.LoadCommand.ExecuteAsync(null));
        Assert.Equal(1, service.ScanCount);
        Assert.Equal(0, service.ExistingLoadCount);
        Assert.Equal(0, profile.TextReadCount);
        Assert.Contains(root, viewModel.ResultSourceDescription, StringComparison.Ordinal);

        var first = Assert.IsType<CatalogItemViewModel>(viewModel.Items.GetItemAt(0));
        viewModel.SelectRowAsync(first, additive: false, range: false).GetAwaiter().GetResult();
        Assert.Equal(1, viewModel.SelectedCount);
        Complete(viewModel.RefreshTextColumnCommand.ExecuteAsync("notes"));
        Assert.Equal(1200, profile.TextReadCount); // The one selected row is not the refresh scope.
        Assert.Equal(1200, viewModel.Items.Count);

        void Complete(Task operation)
        {
            PumpDispatcherUntil(window.Dispatcher, () => operation.IsCompleted, TimeSpan.FromSeconds(6));
            operation.GetAwaiter().GetResult();
        }
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

    private static IDisposable UseDispatcherSynchronizationContext(
        Dispatcher dispatcher) =>
        new DispatcherSynchronizationContextScope(dispatcher);

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

    private static bool PathsEqual(string? first, string? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyProfilesScrolling(Window window, ProfilesPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));

        var tabControl = Assert.IsType<TabControl>(page.FindName("ProfilesTabControl"));
        Assert.Equal(HorizontalAlignment.Stretch, tabControl.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Stretch, tabControl.VerticalContentAlignment);
        var viewModel = Assert.IsType<ProfilesViewModel>(page.DataContext);
        Assert.Equal(ProfileEditorStep.Profile, viewModel.CurrentEditorStep);
        Assert.False(viewModel.PreviousEditorStepCommand.CanExecute(null));
        Assert.True(viewModel.NextEditorStepCommand.CanExecute(null));
        Assert.True(viewModel.IsGuidedMode);
        Assert.NotEmpty(viewModel.GeneratedPatternPreview);
        var templateBox = Assert.IsType<Wpf.Ui.Controls.TextBox>(
            page.FindName("ProfilePathTemplateTextBox"));
        Assert.True(templateBox.IsReadOnly);
        Assert.IsType<Border>(page.FindName("AssignmentPickerPanel"));
        Assert.NotEmpty(viewModel.GuidedPathSegments);
        VerifyGuidedPathButtonAuthoring(window, viewModel);

        PumpLayout(window, page);
        VerifyProfilesFluentControls(page);
        var editorScrollViewer = Assert.IsType<ScrollViewer>(
            page.FindName("ProfileEditorScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), editorScrollViewer.Padding);
        Assert.IsType<Border>(page.FindName("ProfileEditorStepRail"));
        Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("PreviousProfileStepButton"));
        Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("NextProfileStepButton"));
        var userInputEditor = Assert.IsType<Border>(page.FindName("UserInputFieldsEditor"));
        Assert.True(viewModel.AddUserInputFieldCommand.CanExecute(null));
        viewModel.AddUserInputFieldCommand.Execute(null);
        Assert.Single(viewModel.DraftUserInputFields);

        viewModel.NextEditorStepCommand.Execute(null);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.Equal(ProfileEditorStep.PathAndFields, viewModel.CurrentEditorStep);
        Assert.True(viewModel.IsPathAndFieldsStep);
        Assert.Equal(0, editorScrollViewer.VerticalOffset);

        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.ScanOptions);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.Equal(Visibility.Visible, userInputEditor.Visibility);
        Assert.Contains(
            FindVisualChildren<System.Windows.Controls.TextBlock>(userInputEditor),
            static text => text.Text == "행별 사용자 입력");

        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.Review);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.True(viewModel.IsReviewStep);
        Assert.Equal(
            Visibility.Visible,
            Assert.IsType<Border>(page.FindName("ProfileReviewPanel")).Visibility);

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
        var exclusionScopeDescription = Assert.IsType<System.Windows.Controls.TextBlock>(
            page.FindName("DirectoryExclusionScopeDescription"));
        Assert.Contains("마지막 폴더 이름(leaf)", exclusionScopeDescription.Text, StringComparison.Ordinal);
        Assert.Contains("절대경로 전체가 아니라", exclusionScopeDescription.Text, StringComparison.Ordinal);
        Assert.Contains("상위 폴더는 비교하지 않습니다", exclusionScopeDescription.Text, StringComparison.Ordinal);

        foreach (var resourceKey in new[]
                 {
                     "IconOnlyButtonStyle",
                     "GuidedModeChoiceStyle",
                 })
        {
            var style = Assert.IsType<Style>(page.Resources[resourceKey]);
            Assert.NotNull(style.BasedOn);
        }

        var editorFooter = Assert.IsType<Border>(page.FindName("EditorFooter"));
        Assert.IsType<System.Windows.Controls.TextBlock>(page.FindName("EditorFooterStatusText"));
        Assert.Empty(FindVisualChildren<Wpf.Ui.Controls.InfoBar>(editorFooter));

        var modeButtons = FindVisualChildren<RadioButton>(page)
            .Where(static button =>
                System.Windows.Automation.AutomationProperties.GetName(button) is
                    "초보자 모드" or "전문가 모드")
            .ToArray();
        Assert.Equal(2, modeButtons.Length);
        Assert.All(modeButtons, static button =>
            Assert.Equal("ProfileEditorMode", button.GroupName));
    }

    private static void VerifyGuidedPathButtonAuthoring(
        Window window,
        ProfilesViewModel viewModel)
    {
        var nameField = Assert.Single(viewModel.DraftFields);
        viewModel.ClearFieldAssignmentsCommand.Execute(nameField);
        viewModel.SamplePath = @"C:\Archive\2026\0521_Project-A";
        var buildTemplateTask = viewModel.BuildTemplateFromSampleCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            window.Dispatcher,
            () => buildTemplateTask.IsCompleted,
            TimeSpan.FromSeconds(5));
        buildTemplateTask.GetAwaiter().GetResult();
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
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

        viewModel.AddDirectoryExclusionRuleCommand.Execute(null);
        var exclusion = Assert.Single(viewModel.DraftExcludedDirectoryNameRules);
        exclusion.Pattern = @"(?<excludedLeaf>0521_Project-A)";
        Assert.Equal(ProfileRegexMatchMode.Full, exclusion.MatchMode);
    }

    private static void VerifyProfilePlaygroundLayout(
        Window window,
        ProfilePlaygroundPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));
        PumpLayout(window, page);

        var scrollViewer = Assert.IsType<ScrollViewer>(
            page.FindName("ProfilePlaygroundScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), scrollViewer.Padding);
        var inputPanel = Assert.IsType<Grid>(page.FindName("PlaygroundInputPanel"));
        var enabledBinding = BindingOperations.GetBinding(
            inputPanel,
            UIElement.IsEnabledProperty);
        Assert.NotNull(enabledBinding);
        Assert.Equal(nameof(ProfilePlaygroundViewModel.IsReady), enabledBinding.Path.Path);
        Assert.NotNull(page.FindName("PlaygroundRegexDebugExpander"));
        Assert.NotNull(page.FindName("PlaygroundDirectoryExclusionResultPanel"));
        Assert.IsType<ProfilePlaygroundViewModel>(page.DataContext);
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
            DisplayFormat: null)
        {
            ValueMappings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Apollo"] = "아폴로",
            },
        };
        var userInputField = new ProfileFieldDescriptor(
            "review-note",
            string.Empty,
            "검토 메모",
            10,
            Required: false,
            ProfileFieldValueKind.String,
            IsNullable: true,
            ParseFormat: null,
            DisplayFormat: null)
        {
            SourceKind = ProfileFieldSourceKind.UserInput,
            Placeholder = "메모 입력",
        };
        var item = new CatalogItemViewModel(
            new CatalogItem(
                @"C:\Archive\Apollo",
                "Apollo",
                "sample",
                new object(),
                new Dictionary<string, object?> { ["client"] = "Apollo" },
                CoveragePending: false),
            [field, userInputField],
            new TestAppLocalizer());
        var grid = new System.Windows.Controls.DataGrid
        {
            AutoGenerateColumns = false,
            EnableColumnVirtualization = false,
            EnableRowVirtualization = false,
            IsReadOnly = true,
            ItemsSource = new[] { item },
            DataContext = new FilterContext(string.Empty),
        };
        DynamicProfileGrid.SetLocalizer(
            grid,
            new TestAppLocalizer(
                "en-US",
                new Dictionary<string, string>
                {
                    ["Loc.Catalog.Column.Status"] = "Status",
                    ["Loc.Catalog.Column.FolderPath"] = "Folder path",
                    ["Loc.Catalog.Selection.Item"] = "Select item",
                }));
        DynamicProfileGrid.SetFields(grid, new[] { field, userInputField });
        DynamicProfileGrid.SetLayoutStore(
            grid,
            new StubGridLayoutStore(new GridColumnLayout("client", 1, 180)));
        DynamicProfileGrid.SetProfileId(grid, "selection-test");

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

            var selectionColumn = grid.Columns[0];
            Assert.Equal(0, selectionColumn.DisplayIndex);
            Assert.False(selectionColumn.CanUserReorder);
            Assert.False(selectionColumn.CanUserResize);
            Assert.False(selectionColumn.CanUserSort);
            Assert.Equal(52, selectionColumn.Width.Value);
            Assert.Equal(1, grid.FrozenColumnCount);
            Assert.Contains(grid.Columns, column => Equals(column.Header, "Status"));
            Assert.Contains(grid.Columns, column => Equals(column.Header, "Folder path"));
            var clientColumn = Assert.Single(grid.Columns, column => Equals(column.Header, "고객"));
            Assert.Equal(2, clientColumn.DisplayIndex);
            Assert.Equal(180, clientColumn.Width.Value);
            Assert.Single(grid.Columns, column => Equals(column.Header, "검토 메모"));
            var row = Assert.IsType<System.Windows.Controls.DataGridRow>(
                grid.ItemContainerGenerator.ContainerFromItem(item));
            var userInput = Assert.Single(FindVisualChildren<Wpf.Ui.Controls.TextBox>(row));
            Assert.Equal("메모 입력", userInput.PlaceholderText);
            var selector = Assert.Single(
                FindVisualChildren<System.Windows.Controls.CheckBox>(row));
            Assert.Equal("Select item", selector.ToolTip);
            Assert.True(selector.IsVisible);
            Assert.False(selector.IsChecked);
            row.IsSelected = true;
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.True(selector.IsChecked);
            selector.IsChecked = false;
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.False(row.IsSelected);

            var textBlock = Assert.Single(
                FindVisualChildren<System.Windows.Controls.TextBlock>(grid),
                block => TextHighlighting.GetDisplayText(block) == "아폴로");
            Assert.Equal("아폴로", string.Concat(
                textBlock.Inlines.OfType<Run>().Select(static run => run.Text)));

            item.SetShowOriginalValues(true);
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.Equal("Apollo", TextHighlighting.GetDisplayText(textBlock));

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
        private readonly object _gate = new();
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CatalogResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CatalogItem> _reportedItems = [];
        private IProgress<CatalogOperationProgress>? _progress;

        public bool HasStarted => _started.Task.IsCompleted;

        public int ScanAndLoadCallCount { get; private set; }

        public CatalogRequest? LastScanAndLoadRequest { get; private set; }

        public async Task<CatalogResult> DiscoverAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "The desktop default scan must use the persistent index workflow.");

        public Task<CatalogResult> LoadExistingAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<CatalogResult> ScanAndLoadAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanAndLoadCallCount++;
            LastScanAndLoadRequest = request;
            lock (_gate)
            {
                _progress = progress;
            }

            _started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public void ReportMatches(params CatalogItem[] items)
        {
            IProgress<CatalogOperationProgress>? progress;
            int totalItems;
            lock (_gate)
            {
                _reportedItems.AddRange(items);
                progress = _progress;
                totalItems = _reportedItems.Count;
            }

            foreach (var item in items)
            {
                progress?.Report(new CatalogOperationProgress(
                    CatalogOperationPhase.Mapping,
                    $"일치 {totalItems:N0}개를 찾았습니다.")
                {
                    MatchedItem = item,
                });
            }
        }

        public void Complete(
            ProfileDescriptor descriptor,
            params CatalogItem[] finalItems)
        {
            CatalogItem[] items;
            lock (_gate)
            {
                items = finalItems.Length == 0
                    ? _reportedItems.ToArray()
                    : finalItems.ToArray();
            }

            _completion.TrySetResult(new CatalogResult(
                descriptor,
                items,
                [],
                items.Length,
                0,
                false,
                new IndexScanReport(
                    Guid.NewGuid(),
                    Path.GetTempPath(),
                    Path.GetTempPath(),
                    IndexScanStatus.Completed,
                    new IndexScanProgress(
                        Entries: items.Length,
                        Directories: items.Length,
                        ExcludedEntries: 0,
                        SkippedLinks: 0,
                        ErrorCount: 0,
                        Elapsed: TimeSpan.FromSeconds(65.43),
                        PendingDirectories: 0),
                    [],
                    []))
            {
                ExcludedDirectoryCount = 2,
            });
        }
    }

    private sealed class StubLoadedProfile : ILoadedProfile
    {
        private static readonly ProfileFieldDescriptor ClientField = new(
            "client",
            "client",
            "고객",
            10,
            true,
            ProfileFieldValueKind.String,
            true,
            null,
            null);

        public ProfileDescriptor Descriptor { get; } = new(
            "scan-smoke-profile",
            "1.0.0",
            "스캔 스모크 프로필",
            ProfileCandidateKind.Directory,
            [ClientField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            ProfileMapResult.NoMatch();
    }

    private sealed class ThrowingFormattable : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) =>
            throw new InvalidOperationException("Intentional live-display failure.");

        public override string ToString() =>
            throw new InvalidOperationException("Intentional live-display failure.");
    }

    private sealed class RecordingScanCompletionNotifier : IScanCompletionNotifier
    {
        public List<ScanCompletionNotice> Notices { get; } = [];

        public void Notify(ScanCompletionNotice notice) => Notices.Add(notice);
    }

    private sealed class StubGridLayoutStore(params GridColumnLayout[] layouts) : IGridLayoutStore
    {
        private readonly IReadOnlyDictionary<string, GridColumnLayout> _layouts = layouts.ToDictionary(
            static layout => layout.FieldId,
            StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId) => _layouts;

        public void Save(string profileId, IEnumerable<GridColumnLayout> columns)
        {
        }
    }

    private sealed class DispatcherSynchronizationContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public DispatcherSynchronizationContextScope(Dispatcher dispatcher) =>
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

        public void Dispose() =>
            SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed record FilterContext(string FilterText);

    private sealed class WorkspaceLayoutFixture
    {
        private readonly object _profile = new { Id = "layout-sample", DisplayName = "프로젝트 폴더" };
        public WorkspaceLayoutFixture(bool isCatalog)
        {
            SelectedProfile = _profile;
            var fields = new ProfileFieldDescriptor[]
            {
                new("client", "client", "고객", 0, true, ProfileFieldValueKind.String, false, null, null),
                new("project", "project", "프로젝트", 1, true, ProfileFieldValueKind.String, false, null, null),
                new("date", "date", "기준일", 2, false, ProfileFieldValueKind.DateTime, true, "yyyyMMdd", "yyyy-MM-dd"),
                new("note", "", "검토 메모", 3, false, ProfileFieldValueKind.String, true, null, null) { SourceKind = ProfileFieldSourceKind.UserInput },
            };
            Fields = isCatalog ? fields : [];
            Items = Enumerable.Range(0, 12).Select(index =>
            {
                var name = isCatalog ? $"Project-{index + 1:00}" : $"Project-{index + 1:00}.pdf";
                var path = Path.Combine(RootPath, name);
                if (!isCatalog) return (object)new FileSearchItemViewModel(new IndexedPathEntry(path, name, RootPath,
                    IndexedPathKind.File, (index + 1) * 128000, DateTimeOffset.Parse("2026-05-21T09:00:00Z"),
                    DateTimeOffset.Parse("2026-10-13T09:00:00Z"), false), new TestAppLocalizer());
                return new CatalogItemViewModel(new CatalogItem(path, name, "sample", new object(),
                    new Dictionary<string, object?> { ["client"] = index % 2 == 0 ? "Apollo" : "Contoso", ["project"] = name,
                        ["date"] = new DateTime(2026, 5, 21).AddDays(index) }, false), fields, new TestAppLocalizer());
            }).ToArray();
        }
        public bool HasIndex => true;
        public bool HasLoadedItems => true;
        public bool HasSelection => true;
        public bool CanSearch => true;
        public bool CanEditWorkspace => true;
        public bool IsDetailsOpen => false;
        public bool IsBusy => false;
        public bool IsSearching => false;
        public bool IsSetupStateVisible => false;
        public bool IsNoResultsVisible => false;
        public bool HasValueMappings => false;
        public bool UsesQuerySelection => false;
        public int SelectedCount => 1000000;
        public string RootPath { get; set; } = @"C:\Archive\Projects";
        public string DetailsButtonText => "자세히";
        public string ResultSummary => "1,000,000개 항목";
        public string FilterSummary => "100,000개 항목";
        public string StatusMessage => "준비됨";
        public string ProgressMessage => string.Empty;
        public object[] Profiles => [_profile];
        public object? SelectedProfile { get; set; }
        public object? SelectedItem { get; set; }
        public object? SelectedOutputFormat { get; set; }
        public string SearchText { get; set; } = string.Empty;
        public string FilterText { get; set; } = string.Empty;
        public string DatabasePath { get; set; } = @"C:\Indexes\metadata.db";
        public string RefreshScopePath { get; set; } = string.Empty;
        public bool IsFilterOpen { get; set; }
        public bool ShowOriginalValues { get; set; }
        public object? SelectedKind { get; set; }
        public object? SelectedSort { get; set; }
        public object? SelectedCreatedDateFilter { get; set; }
        public object? SelectedModifiedDateFilter { get; set; }
        public double MinimumSizeStep { get; set; }
        public double MaximumSizeStep { get; set; } = 11;
        public DateTime? CreatedFromDate { get; set; }
        public DateTime? CreatedToDate { get; set; }
        public DateTime? ModifiedFromDate { get; set; }
        public DateTime? ModifiedToDate { get; set; }
        public object[] KindChoices => [];
        public object[] SortChoices => [];
        public object[] DateFilterChoices => [];
        public object[] Items { get; }
        public ProfileFieldDescriptor[] Fields { get; }
        public object[] OutputFormats => [];
    }

    private sealed class PlaygroundLayoutFixture
    {
        private readonly object _profile = new { Description = "프로젝트 폴더" };
        public PlaygroundLayoutFixture(string inputPath, IReadOnlyList<ProfileRegexDebugRuleViewModel> debugRules)
        {
            InputPath = inputPath;
            RegexDebugInputPath = inputPath;
            SelectedProfile = _profile;
            RegexDebugRules = debugRules;
        }
        public bool IsReady => true;
        public bool IsBusy => false;
        public string InputPath { get; set; }
        public object[] Profiles => [_profile];
        public object? SelectedProfile { get; set; }
        public string Summary => "규칙에 일치했습니다. 추출한 값과 캡처 그룹을 확인하세요.";
        public ProfileTestResultViewModel[] Rows =>
        [new("고객", "Apollo", "완료"), new("프로젝트", "Project", "완료"), new("기준일", "2026-05-21", "완료")];
        public bool HasRegexDebugRules => true;
        public bool HasDirectoryExclusionTestResult => false;
        public bool HasDirectoryExclusionRegexDebugRules => false;
        public string RegexDebugInputPath { get; }
        public IReadOnlyList<ProfileRegexDebugRuleViewModel> RegexDebugRules { get; }
    }

    private sealed class DiscardDraftPrompt : IDraftDecisionPrompt
    {
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) => Task.FromResult(DraftLeaveDecision.Discard);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public bool TrySetText(string text, out string? errorMessage) { Text = text; errorMessage = null; return true; }
    }

    private sealed class QueryTestProfile : ILoadedProfile
    {
        public int TextReadCount { get; private set; }
        public ProfileDescriptor Descriptor { get; } = new("query-test", "1.0", "Query test", ProfileCandidateKind.Directory,
        [
            new("client", "client", "고객", 0, true, ProfileFieldValueKind.String, false, null, null),
            new("notes", "", "메모", 1, false, ProfileFieldValueKind.String, true, null, null) { SourceKind = ProfileFieldSourceKind.TextFileContent },
        ], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => throw new InvalidOperationException("The persisted projection must be queried directly.");
        public ProfileTextColumnReadResult ReadTextColumn(string fullPath, string fieldId)
        {
            Assert.Equal("notes", fieldId);
            TextReadCount++;
            return new("Updated notes", []);
        }
    }

    private sealed class QueryTestCatalogService(QueryTestProfile profile, string root) : ICatalogService
    {
        public int ScanCount { get; private set; }
        public int ExistingLoadCount { get; private set; }
        public Task<CatalogResult> LoadExistingAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { ExistingLoadCount++; throw new InvalidOperationException("The existing projection should be reused."); }
        public Task<CatalogResult> DiscoverAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogResult> ScanAndLoadAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanCount++;
            Assert.False(request.RetainItems);
            Assert.NotNull(request.ResultWriter);
            for (var index = 0; index < 1200; index++)
            {
                var name = $"Item-{index:0000}";
                var values = new Dictionary<string, object?> { ["client"] = name, ["notes"] = "Cached notes" };
                request.ResultWriter.Append(new CatalogItem(Path.Combine(root, name), name, "test", values, values, false));
            }
            return Task.FromResult(new CatalogResult(profile.Descriptor, [], [], 1200, 0, false, null) { MatchedCount = 1200 });
        }
    }

    private sealed class QueryTestWorkspace(WorkspaceSnapshot initial) : IWorkspaceContext
    {
        public WorkspaceSnapshot Current { get; private set; } = initial;
        public event EventHandler<WorkspaceChangedEventArgs>? Changed;
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Current; Current = snapshot; Changed?.Invoke(this, new(previous, snapshot));
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Func<WorkspaceSnapshot, WorkspaceSnapshot> update, CancellationToken cancellationToken = default) => SaveAsync(update(Current), cancellationToken);
        public Task SetProfileDatabasePathAsync(string profileId, string? databasePath, CancellationToken cancellationToken = default)
        {
            var paths = new Dictionary<string, string>(Current.ProfileDatabasePaths ?? new Dictionary<string, string>());
            if (databasePath is null) paths.Remove(profileId); else paths[profileId] = databasePath;
            return SaveAsync(Current with { ProfileDatabasePaths = paths }, cancellationToken);
        }
    }
}
