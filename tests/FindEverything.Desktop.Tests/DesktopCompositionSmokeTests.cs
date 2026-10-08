using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
                    new AppPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "findeverything-smoke.json")));
            services.AddSingleton(blockingCatalogService);
            services.AddSingleton<ICatalogService>(
                static provider => provider.GetRequiredService<BlockingCatalogService>());
            services.AddSingleton(completionNotifier);
            services.AddSingleton<IScanCompletionNotifier>(completionNotifier);

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
            Assert.True(navigation.Navigate(typeof(FilesPage)));
            VerifyPageScrollContracts(filesPage, catalogPage);
            VerifyCompactSelectionPages(filesPage, catalogPage);
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

        var filesToolbar = Assert.IsType<Border>(filesPage.FindName("SelectionToolbar"));
        var catalogToolbar = Assert.IsType<Border>(catalogPage.FindName("SelectionToolbar"));
        Assert.Equal(Visibility.Collapsed, filesToolbar.Visibility);
        Assert.Equal(Visibility.Collapsed, catalogToolbar.Visibility);
        var exportButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            catalogPage.FindName("ExportSelectionButton"));
        Assert.NotNull(exportButton.Command);
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
        var pageContent = Assert.IsType<Grid>(catalogPage.FindName("CatalogPageContent"));
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
        Assert.True(viewModel.IsPanelOpen);
        Assert.Equal(Visibility.Visible, panel.Visibility);
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
        var notification = Assert.Single(
            provider.GetRequiredService<RecordingScanCompletionNotifier>().Notices);
        Assert.Equal("DB에 저장하지 않는 스캔 완료", notification.Title);
        Assert.Contains("소요 1분 05.43초", notification.Message, StringComparison.Ordinal);

        // The persistent workflow is deliberately separate from direct discovery:
        // it writes through ScanAndLoadAsync and must not open the direct-scan log.
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
            var rejectedIndexAndLoad = catalog.IndexAndLoadCommand.ExecuteAsync(null);
            PumpDispatcherUntil(
                mainWindow.Dispatcher,
                () => rejectedIndexAndLoad.IsCompleted,
                TimeSpan.FromSeconds(3));
            rejectedIndexAndLoad.GetAwaiter().GetResult();
            Assert.Equal(workspaceBeforeRejectedIndex, workspaceContext.Current);
            Assert.Equal(0, blockingService.ScanAndLoadCallCount);
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
        var indexAndLoadTask = catalog.IndexAndLoadCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => indexAndLoadTask.IsCompleted,
            TimeSpan.FromSeconds(3));
        indexAndLoadTask.GetAwaiter().GetResult();
        PumpDispatcherFor(mainWindow.Dispatcher, TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, blockingService.ScanAndLoadCallCount);
        Assert.True(replacedLoad.IsCanceled);
        Assert.Equal(catalog.DatabasePath, blockingService.LastScanAndLoadRequest?.DatabasePath);
        Assert.Equal("인덱싱 후 불러오기 완료", catalog.StatusTitle);
        Assert.False(viewModel.IsPanelOpen);
        Assert.Equal(
            "인덱싱 후 불러오기 완료",
            provider.GetRequiredService<RecordingScanCompletionNotifier>().Notices[^1].Title);
        var savedWorkspace = workspaceContext.Current;
        Assert.Equal(catalog.RootPath, savedWorkspace.RootPath);
        Assert.Equal(catalog.DatabasePath, savedWorkspace.DatabasePath);

        // A local root draft and an external database edit merge field-by-field.
        // The delayed root save must not overwrite the newer shared database path.
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
        Assert.Equal(editedDatabasePath, catalog.DatabasePath);

        // Clearing the required DB field is a validation failure, not a request to
        // silently keep the old shared value while the editor remains blank.
        catalog.DatabasePath = string.Empty;
        var rejectedWorkspaceCommit = catalog.CommitWorkspaceCommand.ExecuteAsync(null);
        PumpDispatcherUntil(
            mainWindow.Dispatcher,
            () => rejectedWorkspaceCommit.IsCompleted,
            TimeSpan.FromSeconds(3));
        rejectedWorkspaceCommit.GetAwaiter().GetResult();
        Assert.Equal(editedDatabasePath, workspaceContext.Current.DatabasePath);
        Assert.Equal("작업 위치 저장 실패", catalog.StatusTitle);

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

        viewModel.NextEditorStepCommand.Execute(null);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.Equal(ProfileEditorStep.PathAndFields, viewModel.CurrentEditorStep);
        Assert.True(viewModel.IsPathAndFieldsStep);
        Assert.Equal(0, editorScrollViewer.VerticalOffset);

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
        var item = new CatalogItemViewModel(
            new CatalogItem(
                @"C:\Archive\Apollo",
                "Apollo",
                "sample",
                new object(),
                new Dictionary<string, object?> { ["client"] = "Apollo" },
                CoveragePending: false),
            [field],
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
        DynamicProfileGrid.SetFields(grid, new[] { field });
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
            Assert.Equal(1, grid.FrozenColumnCount);
            Assert.Contains(grid.Columns, column => Equals(column.Header, "Status"));
            Assert.Contains(grid.Columns, column => Equals(column.Header, "Folder path"));
            var clientColumn = Assert.Single(grid.Columns, column => Equals(column.Header, "고객"));
            Assert.Equal(2, clientColumn.DisplayIndex);
            Assert.Equal(180, clientColumn.Width.Value);
            var row = Assert.IsType<System.Windows.Controls.DataGridRow>(
                grid.ItemContainerGenerator.ContainerFromItem(item));
            var selector = Assert.Single(
                FindVisualChildren<System.Windows.Controls.CheckBox>(row));
            Assert.Equal("Select item", selector.ToolTip);
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
        private CatalogItem[] _finalItems = [];
        private IProgress<CatalogOperationProgress>? _progress;
        private ProfileDescriptor? _descriptor;

        public bool HasStarted => _started.Task.IsCompleted;

        public int ScanAndLoadCallCount { get; private set; }

        public CatalogRequest? LastScanAndLoadRequest { get; private set; }

        public async Task<CatalogResult> DiscoverAsync(
            CatalogRequest request,
            IProgress<CatalogOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _progress = progress;
            }

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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanAndLoadCallCount++;
            LastScanAndLoadRequest = request;
            return Task.FromResult(new CatalogResult(
                _descriptor ?? throw new InvalidOperationException("No profile descriptor."),
                _finalItems,
                [],
                _finalItems.Length,
                0,
                false,
                null));
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
            _descriptor = descriptor;
            CatalogItem[] items;
            lock (_gate)
            {
                items = finalItems.Length == 0
                    ? _reportedItems.ToArray()
                    : finalItems.ToArray();
                _finalItems = items;
            }

            _completion.TrySetResult(new CatalogResult(
                descriptor,
                items,
                [],
                items.Length,
                0,
                false,
                null)
            {
                DiscoveryReport = new DirectoryDiscoveryReport(
                    Path.GetTempPath(),
                    DirectoryDiscoveryStatus.Completed,
                    new DirectoryDiscoveryProgress(
                        Entries: items.Length,
                        Directories: items.Length,
                        PrunedDirectories: 0,
                        SkippedLinks: 0,
                        ErrorCount: 0,
                        Elapsed: TimeSpan.FromSeconds(65.43)),
                    []),
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
}
