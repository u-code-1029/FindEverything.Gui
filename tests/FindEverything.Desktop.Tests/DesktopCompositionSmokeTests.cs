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

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(180));
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
            CatalogFilteringTests.VerifyHighlightingRendering();
            SectionCardRegressionChecks.Verify();
            GridSizingRegressionChecks.Verify();

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Appearance:Theme"] = "System",
                ["Appearance:Backdrop"] = "Auto",
                ["Localization:CultureName"] = LocalizationOptions.KoreanCultureName,
                // This legacy fake deliberately reports two rows and then blocks.
                // Production/default-size batching is covered separately.
                ["Indexing:UiUpdateBatchSize"] = "1",
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
            services.AddSingleton<ICatalogMissingDataPrompt, LaterCatalogMissingDataPrompt>();
            // Keep the existing live-scan/diagnostic regressions on their original
            // fake service. A separate helper exercises the real projection path.
            services.RemoveAll<ICatalogProjectionStore>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            PageResponsivenessChecks.Verify(provider);
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
            Assert.IsType<CardAction>(homePage.FindName("FilesActionCard"));
            Assert.IsType<CardAction>(homePage.FindName("CatalogActionCard"));
            Assert.IsAssignableFrom<CardControl>(homePage.FindName("WorkspaceOverviewCard"));
            Assert.True(navigation.Navigate(typeof(FilesPage)));
            VerifyPageScrollContracts(filesPage, catalogPage);
            VerifyCompactSelectionPages(filesPage, catalogPage);
            VerifyCoreWorkspaceBounds(provider, window);
            WorkspaceTransferUiChecks.Verify(provider, window, (visual, name) => SaveQaImage(visual, name));
            Assert.True(navigation.Navigate(typeof(CatalogPage)));
            Assert.True(navigation.Navigate(typeof(ProfilesPage)));
            VerifyProfilesScrolling(window, profilesPage);
            Assert.True(navigation.Navigate(typeof(ProfilePlaygroundPage)));
            VerifyProfilePlaygroundLayout(window, profilePlaygroundPage);
            Assert.True(navigation.Navigate(typeof(OutputFormatsPage)));
            VerifyOutputFormatEditor(provider, window, outputFormatsPage);
            Assert.True(navigation.Navigate(typeof(SettingsPage)));
            VerifySettingsLayout(window, settingsPage);
            VerifyOperationInfoBar(provider, window);
            VerifyScanConsole(provider, window, catalogPage);
            VerifyProjectionQueries(provider, window);
            LiveFileUiChecks.Verify();
            SelectionImportUiChecks.Verify();
            ProfilesValidationUiChecks.Verify((visual, name) => SaveQaImage(visual, name));
            UiRenderingLifetimeChecks.Verify();
            ReadOnlyCellTemplateChecks.Verify();
            WorkspacePerformanceChecks.Verify(provider);
            CatalogColumnFilterUiChecks.Verify(provider);
            WorkspaceTransferBehaviorChecks.Verify();
            CaptureOptionalEnglishQaScreenshots(window);
            VerifyDynamicGridHighlighting();
            VerifyCatalogDensity(appearance, window, catalogPage);
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

        var filesGrid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(
            filesPage.FindName("ResultsGrid"));
        var catalogGrid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(
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

        var filesToolbar = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(filesPage.FindName("SelectionToolbar"));
        var catalogToolbar = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(catalogPage.FindName("SelectionToolbar"));
        Assert.Equal(Visibility.Collapsed, filesToolbar.Visibility);
        Assert.Equal(Visibility.Collapsed, catalogToolbar.Visibility);
        var exportButton = Assert.IsType<Wpf.Ui.Controls.Button>(
            catalogPage.FindName("ExportSelectionButton"));
        Assert.NotNull(exportButton.Command);
        foreach (var page in new Page[] { filesPage, catalogPage })
        {
            foreach (var (name, content) in new[]
            {
                ("SelectAllResultsButton", "전체 선택"),
                ("ClearAllResultsButton", "전체 선택 해제"),
                ("InvertResultsSelectionButton", "선택 반전"),
            })
            {
                var button = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName(name));
                Assert.Equal(content, button.Content);
                Assert.NotNull(button.Command);
            }
        }
        var profileLabel = Assert.IsType<System.Windows.Controls.Label>(
            catalogPage.FindName("CatalogProfileLabel"));
        Assert.Equal("프로필", profileLabel.Content);
        Assert.Same(catalogPage.FindName("CatalogProfileComboBox"), profileLabel.Target);
    }

    private static void VerifyCoreWorkspaceBounds(IServiceProvider provider, MainWindow window)
    {
        // Navigation's attached content host owns the cached page's size. Directly
        // arranging that page is subsequently overwritten by its parent window.
        // Separate, unattached instances exercise the actual production XAML at
        // an exact viewport without changing the acceptance thresholds.
        var pages = new Page[]
        {
            new FilesPage(provider.GetRequiredService<FileSearchViewModel>()),
            new CatalogPage(provider.GetRequiredService<CatalogViewModel>(),
                provider.GetRequiredService<ScanConsoleViewModel>(), provider.GetRequiredService<IGridLayoutStore>()),
        };
        foreach (var page in pages)
        {
            var originalContext = page.DataContext;
            try
            {
                page.DataContext = new WorkspaceLayoutFixture(page is CatalogPage)
                {
                    // Even a count retry must fit without shrinking the result
                    // workspace below its compact-window acceptance threshold.
                    CanRetryResultCount = page is FilesPage,
                };
                foreach (var (size, minimumRatio) in new[]
                         {
                             (new Size(1140, 790), 0.70),
                             (new Size(660, 600), 0.60),
                         })
                {
                    page.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
                    ArrangeWorkspace(page, size);
                    SaveQaImage(page, $"{page.GetType().Name}-{(size.Width >= 900 ? "wide" : "small")}-workspace.png");
                    Assert.InRange(page.ActualWidth, size.Width - 0.01, size.Width + 0.01);
                    Assert.InRange(page.ActualHeight, size.Height - 0.01, size.Height + 0.01);
                    var grid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(page.FindName("ResultsGrid"));
                    var toolbar = Assert.IsAssignableFrom<Wpf.Ui.Controls.CardControl>(page.FindName("SelectionToolbar"));
                    if (page is CatalogPage)
                    {
                        var clearFilters = Assert.Single(FindVisualChildren<Wpf.Ui.Controls.Button>(page),
                            button => ReferenceEquals(button.Command, ((WorkspaceLayoutFixture)page.DataContext).ClearColumnFiltersCommand));
                        Assert.Equal(Visibility.Collapsed, Assert.IsType<WrapPanel>(clearFilters.Parent).Visibility);
                    }
                    Assert.True(grid.ActualHeight / page.ActualHeight >= minimumRatio,
                        $"{page.GetType().Name}: results occupy {grid.ActualHeight / page.ActualHeight:P1} at {size}.");
                    Assert.True(toolbar.ActualHeight > 0);
                    var format = Assert.IsType<System.Windows.Controls.ComboBox>(page.FindName(
                        page is CatalogPage ? "CatalogOutputFormatComboBox" : "FilesOutputFormatComboBox"));
                    Assert.Equal(280, format.Width);
                    Assert.Equal(240, format.MinWidth);
                    Assert.True(format.ActualWidth >= 240, "Output formats must remain readable in a narrow footer.");
                    var selectionMenu = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("SelectionActionsMenuButton"));
                    Assert.NotNull(selectionMenu.ContextMenu);
                    Assert.Equal(3, selectionMenu.ContextMenu.Items.Count);
                    Assert.Equal(size.Width < 1040 ? Visibility.Visible : Visibility.Collapsed, selectionMenu.Visibility);
                    var additionalMenu = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("AdditionalActionsMenuButton"));
                    Assert.NotNull(additionalMenu.ContextMenu);
                    Assert.Equal(page is CatalogPage ? 2 : 1, additionalMenu.ContextMenu.Items.Count);
                    Assert.False(toolbar.Focusable);
                    var gridBottom = grid.TranslatePoint(new Point(0, grid.ActualHeight), page).Y;
                    var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), page).Y;
                    Assert.True(toolbarTop >= gridBottom, "Selection actions must not cover the last row or scrollbar.");
                    var header = grid.Columns[0].Header as Wpf.Ui.Controls.Button;
                    Assert.NotNull(header);
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(header)));
                    Assert.False(ResultGridInput.IsInteractiveOrigin(new System.Windows.Controls.TextBlock()));
                    Assert.True(ResultGridInput.IsInteractiveOrigin(new System.Windows.Controls.CheckBox()));
                    Assert.True(ResultGridInput.IsInteractiveOrigin(new Wpf.Ui.Controls.TextBox()));
                    if (size.Width < 900)
                    {
                        var scroll = Assert.Single(FindVisualChildren<ScrollViewer>(grid),
                            viewer => viewer.Name == "DG_ScrollViewer");
                        Assert.True(scroll.ScrollableWidth > 0, "Narrow results must scroll instead of squeezing metadata columns.");
                        Assert.Equal(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility);
                        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
                        ArrangeWorkspace(page, size);
                        Assert.True(scroll.HorizontalOffset > 0);
                        var firstRow = Assert.IsType<System.Windows.Controls.DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                        var selector = Assert.Single(FindVisualChildren<System.Windows.Controls.CheckBox>(firstRow));
                        var selectorLeft = selector.TranslatePoint(new Point(0, 0), grid).X;
                        Assert.InRange(selectorLeft, 0, grid.Columns[0].ActualWidth);
                        Assert.True(selectorLeft + selector.ActualWidth <= grid.Columns[0].ActualWidth,
                            "The frozen selection checkbox must remain fully visible after horizontal scrolling.");
                        if (page is CatalogPage)
                        {
                            Assert.True(selectorLeft >= 12,
                                "The structured checkbox needs a left gutter after horizontal scrolling.");
                            Assert.True(grid.Columns[0].ActualWidth - selectorLeft - selector.ActualWidth >= 12,
                                "The structured checkbox needs a right gutter after horizontal scrolling.");
                            Assert.Equal(24, selector.MinWidth);
                        }
                        scroll.ScrollToHorizontalOffset(0);
                        ArrangeWorkspace(page, size);
                    }
                }
                VerifyResultRowContrast(provider, window, page);
            }
            finally
            {
                page.DataContext = originalContext;
                page.InvalidateMeasure();
            }
        }
    }

    private static void ArrangeWorkspace(Page page, Size size)
    {
        // SizeChanged switches the compact command/footer layout. Let those
        // invalidations and bindings settle before the second exact-size pass.
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
        PumpDispatcherFor(page.Dispatcher, TimeSpan.FromMilliseconds(80));
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
    }

    private static void VerifyResultRowContrast(IServiceProvider provider, MainWindow window, Page page)
    {
        var appearance = provider.GetRequiredService<IAppearanceService>();
        var fixture = Assert.IsType<WorkspaceLayoutFixture>(page.DataContext);
        fixture.UsesQuerySelection = true;
        if (fixture.Items[0] is FileSearchItemViewModel file) file.IsSelected = true;
        if (fixture.Items[0] is CatalogItemViewModel catalog) catalog.IsSelected = true;
        // Rebind this non-notifying test fixture's query-selection mode.
        page.DataContext = null;
        page.DataContext = fixture;
        // WPF broadcasts application resource changes to presentation roots,
        // not unattached measure-only trees. Contrast therefore uses a real
        // presentation source, while the bounds fixture remains exact-size.
        var host = new Window
        {
            Width = 1140, Height = 790, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Opacity = 0, ShowInTaskbar = false,
            Content = page,
        };
        try
        {
            host.Show();
            PumpLayout(host, page);
            Assert.NotNull(PresentationSource.FromVisual(page));
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                appearance.Apply(window, new AppearanceOptions { Theme = theme, Backdrop = BackdropPreference.None });
                PumpLayout(host, page);
                SaveQaImage(page, $"{page.GetType().Name}-{theme.ToString().ToLowerInvariant()}-contrast.png");
                var grid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(page.FindName("ResultsGrid"));
                foreach (var index in new[] { 0, 1 })
                {
                    var row = Assert.IsType<System.Windows.Controls.DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth),
                        (int)Math.Ceiling(row.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var backdrop = new DrawingVisual();
                    using (var drawing = backdrop.RenderOpen())
                        drawing.DrawRectangle((Brush)page.FindResource("SolidBackgroundFillColorBaseBrush"),
                            null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                    bitmap.Render(backdrop);
                    bitmap.Render(row);
                    var pixel = new byte[4];
                    // Blank space before the checkbox samples the painted row,
                    // not the checkbox, text, header, or selection footer.
                    bitmap.CopyPixels(new Int32Rect(6, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                    var background = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
                    var foreground = Assert.IsType<SolidColorBrush>(row.Foreground).Color;
                    var alpha = foreground.A / 255d;
                    var paintedForeground = Color.FromRgb(
                        (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
                        (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
                        (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
                    var first = RelativeLuminance(paintedForeground);
                    var second = RelativeLuminance(background);
                    var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                    Assert.True(contrast >= 4.5,
                        $"{page.GetType().Name} {theme} row {index}: rendered text contrast {contrast:N2}:1; " +
                        $"foreground={foreground}, painted foreground={paintedForeground}, background={background}, " +
                        $"theme foreground={(page.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush)?.Color}, " +
                        $"loaded={row.IsLoaded}, bitmap alpha={pixel[3]}.");
                }
            }
        }
        finally
        {
            host.Content = null;
            host.Close();
            appearance.Apply(window, new AppearanceOptions { Theme = ThemePreference.Light, Backdrop = BackdropPreference.None });
        }

        static double RelativeLuminance(Color color)
        {
            static double Linear(byte value)
            {
                var channel = value / 255d;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
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
            Capture(files, new WorkspaceLayoutFixture(isCatalog: false), "files-small.png");
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
            PumpLayout(window, window);
        }

        void Capture(Page page, object? fixture, string fileName)
        {
            var original = page.DataContext;
            try
            {
                if (fixture is not null) page.DataContext = fixture;
                var shell = Assert.IsType<NavigationView>(window.FindName("RootNavigation"));
                // Navigate is an intentional no-op for the currently displayed
                // page. Repeated size/theme captures do not change navigation.
                if ((shell.SelectedItem as NavigationViewItem)?.TargetPageType != page.GetType())
                    Assert.True(navigation.Navigate(page.GetType()));
                // WPF-UI's native Expander takes 333 ms to finish revealing its
                // content. A screenshot at 80 ms captures an intentionally
                // translated/clipped caption, not the settled page layout.
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(page is ProfilePlaygroundPage ? 400 : 80));
                PumpLayout(window, page);
                if (fixture is WorkspaceLayoutFixture
                    && page.FindName("ResultsGrid") is System.Windows.Controls.DataGrid sampleGrid)
                {
                    sampleGrid.SelectAll();
                    foreach (var item in sampleGrid.Items.OfType<FileSearchItemViewModel>()) item.IsSelected = true;
                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                    PumpLayout(window, page);
                }
                SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content), fileName);
            }
            finally { page.DataContext = original; page.InvalidateMeasure(); }
        }
    }

    private static void CaptureOptionalEnglishQaScreenshots(MainWindow originalWindow)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR"))) return;
        // Language changes restart the app. Both static XAML resources and the
        // localizer's immutable snapshot need a fresh composition, not a tree walk.
        LocalizationBootstrapper.Apply(System.Windows.Application.Current,
            new LocalizationOptions { CultureName = LocalizationOptions.EnglishCultureName });
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Appearance:Theme"] = "Light", ["Appearance:Backdrop"] = "None",
            ["Localization:CultureName"] = LocalizationOptions.EnglishCultureName,
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure().AddProfileRuntime()
            .AddDesktopPresentation(configuration, new AppPaths(
                Path.Combine(Path.GetTempPath(), $"findeverything-english-{Guid.NewGuid():N}"),
                Path.Combine(Path.GetTempPath(), $"findeverything-english-settings-{Guid.NewGuid():N}.json")));
        services.AddSingleton<IDraftDecisionPrompt, DiscardDraftPrompt>();
        services.AddSingleton<ICatalogMissingDataPrompt, LaterCatalogMissingDataPrompt>();
        services.RemoveAll<ICatalogProjectionStore>();
        using var provider = services.BuildServiceProvider();
        MainWindow? window = null;
        try
        {
            var localizer = provider.GetRequiredService<IAppLocalizer>();
            Assert.Equal(LocalizationOptions.EnglishCultureName, localizer.Culture.Name);
            window = provider.GetRequiredService<MainWindow>();
            var files = provider.GetRequiredService<FilesPage>();
            var catalog = provider.GetRequiredService<CatalogPage>();
            files.DataContext = new WorkspaceLayoutFixture(false, localizer);
            catalog.DataContext = new WorkspaceLayoutFixture(true, localizer);
            Assert.Equal("Find files", files.Title);
            Assert.Equal("Profile", Assert.IsType<Label>(catalog.FindName("CatalogProfileLabel")).Content);
            var shell = Assert.IsType<NavigationView>(window.FindName("RootNavigation"));
            Assert.Equal("File search", shell.MenuItems.OfType<NavigationViewItem>()
                .Single(item => item.TargetPageType == typeof(FilesPage)).Content);
            Assert.Equal("Structured view", shell.MenuItems.OfType<NavigationViewItem>()
                .Single(item => item.TargetPageType == typeof(CatalogPage)).Content);
            var navigation = provider.GetRequiredService<INavigationService>();
            window.Show();
            foreach (var size in new[] { new Size(1360, 860), new Size(760, 640) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (var page in new Page[] { files, catalog })
                {
                    navigation.Navigate(page.GetType());
                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                    PumpLayout(window, page);
                    if (page.FindName("ResultsGrid") is System.Windows.Controls.DataGrid sampleGrid)
                    {
                        sampleGrid.SelectAll();
                        foreach (var item in sampleGrid.Items.OfType<FileSearchItemViewModel>()) item.IsSelected = true;
                        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                        PumpLayout(window, page);
                        if (page is CatalogPage)
                            Assert.Contains(sampleGrid.Columns, column =>
                                Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "Folder path"));
                    }
                    var name = $"{(page is CatalogPage ? "catalog" : "files")}-en-{(size.Width > 900 ? "wide" : "small")}";
                    SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content), name + ".png");
                    if (size.Width < 900) SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content), name + "-200percent-render.png", 2);
                }
            }
        }
        finally
        {
            if (window is not null)
            {
                window.Close();
                PumpDispatcherUntil(window.Dispatcher, () => !window.IsVisible, TimeSpan.FromSeconds(5));
            }
            LocalizationBootstrapper.Apply(System.Windows.Application.Current,
                new LocalizationOptions { CultureName = LocalizationOptions.KoreanCultureName });
            originalWindow.Activate();
        }
    }

    private static void SaveQaImage(FrameworkElement visual, string fileName, double renderScale = 1)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        visual.UpdateLayout();
        var width = Math.Max(1, checked((int)Math.Ceiling(visual.ActualWidth)));
        var height = Math.Max(1, checked((int)Math.Ceiling(visual.ActualHeight)));
        var bitmap = new RenderTargetBitmap(checked((int)Math.Ceiling(width * renderScale)),
            checked((int)Math.Ceiling(height * renderScale)), 96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
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
        var resultsGrid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(
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
                Assert.IsAssignableFrom<System.Windows.Controls.MenuItem>(item).Header),
            item => Assert.Equal(
                "경로만 복사",
                Assert.IsAssignableFrom<System.Windows.Controls.MenuItem>(item).Header));
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
        PumpDispatcherUntil(mainWindow.Dispatcher,
            () => catalog.Items.Count == 2, TimeSpan.FromSeconds(3));
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
        VerifyCatalogPointerSelection(provider, window, viewModel);

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

    private static void VerifyCatalogPointerSelection(
        IServiceProvider provider, Window mainWindow, CatalogViewModel viewModel)
    {
        var page = new CatalogPage(viewModel, provider.GetRequiredService<ScanConsoleViewModel>(),
            provider.GetRequiredService<IGridLayoutStore>());
        var host = new Window { Width = 1000, Height = 600, Content = page };
        try
        {
            host.Show();
            PumpLayout(host, page);
            var grid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(page.FindName("ResultsGrid"));
            viewModel.ClearSelectionCommand.Execute(null);
            var first = Assert.IsType<CatalogItemViewModel>(grid.Items[0]);
            var second = Assert.IsType<CatalogItemViewModel>(grid.Items[1]);
            ClickRow(0);
            Assert.True(first.IsSelected);
            Assert.Equal(1, viewModel.SelectedCount);
            ClickRow(1);
            Assert.True(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Equal(2, viewModel.SelectedCount);
            ClickRow(0);
            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Equal(1, viewModel.SelectedCount);

            var header = Assert.IsType<Wpf.Ui.Controls.Button>(grid.Columns[0].Header);
            Assert.Same(viewModel.InvertSelectionCommand, header.Command);
            header.Command!.Execute(null);
            Assert.Equal(1199, viewModel.SelectedCount);
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            var row = Assert.IsType<System.Windows.Controls.DataGridRow>(
                grid.ItemContainerGenerator.ContainerFromIndex(0));
            var checkbox = Assert.Single(FindVisualChildren<System.Windows.Controls.CheckBox>(row));
            Assert.True(checkbox.IsChecked == true);
            Assert.Equal(new Thickness(0, 2, 0, 2), row.Margin);
            Assert.Equal(new Thickness(0), row.Padding);

            // Interactive descendants must never reach the ordinary row gesture.
            var count = viewModel.SelectedCount;
            InvokePointer("OnResultsGridPreviewMouseLeftButtonDown", checkbox);
            InvokePointer("OnResultsGridPreviewMouseLeftButtonUp", checkbox);
            Assert.Equal(count, viewModel.SelectedCount);
            var textCell = FindVisualChildren<Wpf.Ui.Controls.TextBox>(row).First(editor => editor.IsReadOnly);
            textCell.Focus();
            textCell.SelectAll();
            var selectedText = textCell.SelectedText;
            InvokePointer("OnResultsGridPreviewMouseLeftButtonDown", textCell);
            InvokePointer("OnResultsGridPreviewMouseLeftButtonUp", textCell);
            var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, System.Windows.Input.MouseButton.Right)
            {
                RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent, Source = textCell,
            };
            typeof(CatalogPage).GetMethod("OnResultsGridPreviewMouseRightButtonDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(page, [grid, rightClick]);
            Assert.Equal(count, viewModel.SelectedCount);
            Assert.Equal(selectedText, textCell.SelectedText);
            Assert.NotNull(textCell.ContextMenu);
            viewModel.ClearSelectionCommand.Execute(null);

            void ClickRow(int index)
            {
                grid.ScrollIntoView(grid.Items[index]);
                PumpLayout(host, page);
                var container = Assert.IsType<System.Windows.Controls.DataGridRow>(
                    grid.ItemContainerGenerator.ContainerFromIndex(index));
                var rowSurface = Assert.Single(FindVisualChildren<DataGridCell>(container),
                    cell => ReferenceEquals(cell.Column, grid.Columns[0]));
                InvokePointer("OnResultsGridPreviewMouseLeftButtonDown", rowSurface);
                InvokePointer("OnResultsGridPreviewMouseLeftButtonUp", rowSurface);
                PumpLayout(host, page);
            }

            void InvokePointer(string methodName, UIElement source)
            {
                var args = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = methodName.EndsWith("Down", StringComparison.Ordinal)
                        ? System.Windows.Input.Mouse.PreviewMouseDownEvent
                        : System.Windows.Input.Mouse.PreviewMouseUpEvent,
                    Source = source,
                };
                typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!
                    .SetValue(args, 1);
                typeof(CatalogPage).GetMethod(methodName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(page, [grid, args]);
            }
        }
        finally
        {
            host.Close();
            mainWindow.Activate();
        }
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

        var tabControl = Assert.IsAssignableFrom<TabControl>(page.FindName("ProfilesTabControl"));
        tabControl.ApplyTemplate();
        var baseTabStyle = Assert.IsType<Style>(page.FindResource(typeof(TabControl)));
        Assert.Same(baseTabStyle, Assert.IsType<Style>(tabControl.Style).BasedOn);
        Assert.True(tabControl.OverridesDefaultStyle);
        Assert.IsType<Border>(tabControl.Template.FindName("Border", tabControl));
        foreach (var tab in tabControl.Items.OfType<Wpf.Ui.Controls.TabViewItem>())
        {
            tab.ApplyTemplate();
            Assert.Same(Assert.IsType<Style>(page.FindResource(typeof(TabItem))), Assert.IsType<Style>(tab.Style).BasedOn);
            Assert.True(tab.OverridesDefaultStyle);
            Assert.IsType<Border>(tab.Template.FindName("Border", tab));
        }
        Assert.Equal(HorizontalAlignment.Stretch, tabControl.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Stretch, tabControl.VerticalContentAlignment);
        var viewModel = Assert.IsType<ProfilesViewModel>(page.DataContext);
        Assert.Equal(ProfileEditorStep.Profile, viewModel.CurrentEditorStep);
        Assert.False(viewModel.PreviousEditorStepCommand.CanExecute(null));
        Assert.True(viewModel.NextEditorStepCommand.CanExecute(null));
        Assert.True(viewModel.IsGuidedMode);
        Assert.NotEmpty(viewModel.GeneratedPatternPreview);
        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.PathAndFields);
        PumpLayout(window, page);
        var templateBox = Assert.IsType<Wpf.Ui.Controls.TextBox>(
            page.FindEditorElement<FrameworkElement>("ProfilePathTemplateTextBox"));
        Assert.True(templateBox.IsReadOnly);
        page.ActiveEditorList!.ScrollToSection("AssignmentPicker");
        PumpLayout(window, page);
        Assert.IsAssignableFrom<CardControl>(page.FindEditorElement<FrameworkElement>("AssignmentPickerPanel"));
        Assert.NotEmpty(viewModel.GuidedPathSegments);
        VerifyGuidedPathButtonAuthoring(window, viewModel);

        PumpLayout(window, page);
        VerifyProfilesFluentControls(page);
        var editorScrollViewer = Assert.IsAssignableFrom<ScrollViewer>(page.EditorScrollViewer);
        Assert.Equal(new Thickness(0, 0, 16, 0), editorScrollViewer.Padding);
        Assert.IsAssignableFrom<CardControl>(page.FindName("ProfileEditorStepRail"));
        Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("PreviousProfileStepButton"));
        Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("NextProfileStepButton"));
        Assert.True(viewModel.AddUserInputFieldCommand.CanExecute(null));
        viewModel.AddUserInputFieldCommand.Execute(null);
        Assert.Single(viewModel.DraftUserInputFields);

        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.Profile);
        viewModel.NextEditorStepCommand.Execute(null);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.Equal(ProfileEditorStep.PathAndFields, viewModel.CurrentEditorStep);
        Assert.True(viewModel.IsPathAndFieldsStep);
        Assert.Equal(0, Assert.IsAssignableFrom<ScrollViewer>(page.EditorScrollViewer).VerticalOffset);

        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.DataSources);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        page.ActiveEditorList!.ScrollToSection("UserInputFields");
        PumpLayout(window, page);
        var userInputEditor = Assert.IsAssignableFrom<CardControl>(page.FindEditorElement<FrameworkElement>("UserInputFieldsEditor"));
        Assert.Equal(Visibility.Visible, userInputEditor.Visibility);
        Assert.Contains(
            FindVisualChildren<System.Windows.Controls.TextBlock>(userInputEditor),
            static text => text.Text == "행별 사용자 입력");

        viewModel.GoToEditorStepCommand.Execute(ProfileEditorStep.Review);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        Assert.True(viewModel.IsReviewStep);
        Assert.Equal(
            Visibility.Visible,
            Assert.IsAssignableFrom<CardControl>(page.FindEditorElement<FrameworkElement>("ProfileReviewPanel")).Visibility);

        tabControl.SelectedIndex = 1;
        PumpLayout(window, page);
        VerifyReadOnlyProfileInspectionBindings(window, page, viewModel);
        var loadStatusScrollViewer = Assert.IsAssignableFrom<ScrollViewer>(
            page.FindName("ProfileLoadStatusScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), loadStatusScrollViewer.Padding);
        VerifyScrollable(window, page, loadStatusScrollViewer);

        tabControl.SelectedIndex = 0;
        PumpLayout(window, page);
    }

    private static void VerifyReadOnlyProfileInspectionBindings(
        Window window,
        ProfilesPage page,
        ProfilesViewModel viewModel)
    {
        // Empty inspection grids cannot expose WPF's CheckReadOnly failure.
        // Populate every getter-only boolean with both checked and unchecked rows.
        var previousProfiles = viewModel.Profiles;
        var previousSelection = viewModel.SelectedProfile;
        var descriptor = new ProfileDescriptor(
            "inspection-binding-smoke",
            "1.0.0",
            "읽기 전용 프로필 검사",
            ProfileCandidateKind.Directory,
            [
                new("required", "required", "필수", 1, true,
                    ProfileFieldValueKind.String, false, null, null),
                new("optional", "optional", "선택", 2, false,
                    ProfileFieldValueKind.String, true, null, null),
            ],
            [
                new(1, "sensitive", ".*", ProfileRegexMatchMode.Full, false, 250),
                new(2, "insensitive", ".*", ProfileRegexMatchMode.Full, true, 250),
            ])
        {
            TextFileFields =
            [
                new(1, "required-text", "필수 텍스트", @"^required\.txt$",
                    ProfileRegexMatchMode.Full, false, 250, true, 1024),
                new(2, "optional-text", "선택 텍스트", @"^optional\.txt$",
                    ProfileRegexMatchMode.Full, true, 250, false, 1024),
            ],
            ExcludedDirectoryNameRules =
            [
                new(1, "sensitive", "temp", ProfileRegexMatchMode.Full, false, 250),
                new(2, "insensitive", "cache", ProfileRegexMatchMode.Full, true, 250),
            ],
        };
        var inspectionProfile = new ProfileSummaryViewModel(descriptor);
        try
        {
            viewModel.Profiles = [inspectionProfile];
            viewModel.SelectedProfile = inspectionProfile;
            PumpLayout(window, page);

            string[] gridNames =
            [
                "ProfileFieldsInspectionGrid",
                "ProfileTextFileFieldsInspectionGrid",
                "ProfileRulesInspectionGrid",
                "ProfileExclusionsInspectionGrid",
            ];
            var checkboxColumnCount = 0;
            foreach (var gridName in gridNames)
            {
                var grid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(page.FindName(gridName));
                Assert.True(grid.IsReadOnly);
                Assert.Equal(2, grid.Items.Count);
                foreach (var column in grid.Columns.OfType<DataGridCheckBoxColumn>())
                {
                    checkboxColumnCount++;
                    var binding = Assert.IsType<Binding>(column.Binding);
                    Assert.Equal(BindingMode.OneWay, binding.Mode);
                    foreach (var item in grid.Items)
                    {
                        grid.ScrollIntoView(item, column);
                        PumpLayout(window, page);
                        var property = item.GetType().GetProperty(binding.Path.Path);
                        Assert.NotNull(property);
                        Assert.False(property.CanWrite);
                        var expected = Assert.IsType<bool>(property.GetValue(item));
                        var checkbox = Assert.IsAssignableFrom<System.Windows.Controls.CheckBox>(
                            column.GetCellContent(item));
                        Assert.Equal(expected, checkbox.IsChecked);
                        var expression = checkbox.GetBindingExpression(
                            System.Windows.Controls.CheckBox.IsCheckedProperty);
                        Assert.NotNull(expression);
                        Assert.Equal(BindingStatus.Active, expression.Status);
                    }
                }
            }

            Assert.Equal(6, checkboxColumnCount);
        }
        finally
        {
            viewModel.Profiles = previousProfiles;
            viewModel.SelectedProfile = previousSelection;
            PumpLayout(window, page);
        }
    }

    private static void VerifyProfilesFluentControls(ProfilesPage page)
    {
        var vm = (ProfilesViewModel)page.DataContext;
        var previousStep = vm.CurrentEditorStep;
        vm.GoToEditorStepCommand.Execute(ProfileEditorStep.ScanOptions);
        PumpDispatcherFor(page.Dispatcher, TimeSpan.FromMilliseconds(30));
        var exclusionScopeDescription = Assert.IsAssignableFrom<System.Windows.Controls.TextBlock>(
            page.FindEditorElement<FrameworkElement>("DirectoryExclusionScopeDescription"));
        Assert.Contains("마지막 폴더 이름(leaf)", exclusionScopeDescription.Text, StringComparison.Ordinal);
        Assert.Contains("절대경로 전체가 아니라", exclusionScopeDescription.Text, StringComparison.Ordinal);
        Assert.Contains("상위 폴더는 비교하지 않습니다", exclusionScopeDescription.Text, StringComparison.Ordinal);
        vm.GoToEditorStepCommand.Execute(previousStep);
        PumpDispatcherFor(page.Dispatcher, TimeSpan.FromMilliseconds(30));

        foreach (var resourceKey in new[]
                 {
                     "IconOnlyButtonStyle",
                     "GuidedModeChoiceStyle",
                 })
        {
            var style = Assert.IsType<Style>(page.Resources[resourceKey]);
            Assert.NotNull(style.BasedOn);
        }

        var editorFooter = Assert.IsAssignableFrom<CardControl>(page.FindName("EditorFooter"));
        Assert.IsAssignableFrom<System.Windows.Controls.TextBlock>(page.FindName("EditorFooterStatusText"));
        Assert.Empty(FindVisualChildren<Wpf.Ui.Controls.InfoBar>(editorFooter));

        vm.GoToEditorStepCommand.Execute(ProfileEditorStep.Profile);
        PumpDispatcherFor(page.Dispatcher, TimeSpan.FromMilliseconds(30));
        var modeButtons = FindVisualChildren<RadioButton>(page)
            .Where(static button =>
                System.Windows.Automation.AutomationProperties.GetName(button) is
                    "초보자 모드" or "전문가 모드")
            .ToArray();
        Assert.Equal(2, modeButtons.Length);
        Assert.All(modeButtons, static button =>
            Assert.Equal("ProfileEditorMode", button.GroupName));
        vm.GoToEditorStepCommand.Execute(previousStep);
        PumpDispatcherFor(page.Dispatcher, TimeSpan.FromMilliseconds(30));
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
        PumpDispatcherUntil(window.Dispatcher, () => viewModel.PendingPreview.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.False(viewModel.CanUseGuidedMode);
        rule.StopTraversalGroupsText = "field_1Year, field_1MonthDay";
        PumpDispatcherUntil(window.Dispatcher, () => viewModel.PendingPreview.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.True(viewModel.CanUseGuidedMode);

        dateField.GroupName = "legacyDate";
        var validate = viewModel.ValidateDraftCommand.ExecuteAsync(null);
        PumpDispatcherUntil(window.Dispatcher, () => validate.IsCompleted, TimeSpan.FromSeconds(5));
        validate.GetAwaiter().GetResult();
        Assert.Contains("동시에", viewModel.EditorStatusMessage);
        dateField.GroupName = string.Empty;
        PumpDispatcherUntil(window.Dispatcher, () => viewModel.PendingPreview.IsCompleted, TimeSpan.FromSeconds(5));
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

        var scrollViewer = Assert.IsAssignableFrom<ScrollViewer>(
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
        var originalContext = page.DataContext;
        var originalWidth = window.Width;
        try
        {
            var input = @"C:\Archive\Clients\Apollo\2026\0521_Project";
            var rules = new[] { new ProfileRegexRuleManifest
            {
                Id = "project-folders", Pattern = @".*[\\/](?<client>Apollo)[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<project>[^\\/]+)$",
                MatchMode = ProfileRegexMatchMode.Full, TimeoutMilliseconds = 100,
            } };
            page.DataContext = new PlaygroundLayoutFixture(input, ProfileRegexDebugBuilder.Build(input, rules));
            window.Width = 1360;
            PumpLayout(window, page);
            var debugScroll = Assert.IsAssignableFrom<ScrollViewer>(page.FindName("PlaygroundDebugScrollViewer"));
            debugScroll.ScrollToTop();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            PumpLayout(window, page);
            var card = Assert.IsType<SectionCard>(page.FindName("PlaygroundRegexDebugCard"));
            var caption = Assert.IsAssignableFrom<System.Windows.Controls.TextBlock>(page.FindName("PlaygroundRegexDebugCaption"));
            var expander = Assert.IsAssignableFrom<Expander>(page.FindName("PlaygroundRegexDebugExpander"));
            var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(
                expander.Template.FindName("ExpanderToggleButton", expander));
            Assert.True(caption.ActualHeight > 0);
            Assert.InRange(caption.TranslatePoint(new Point(), card).Y, 16, 18);
            var headerBottom = toggle.TranslatePoint(new Point(0, toggle.ActualHeight), debugScroll).Y;
            var captionTop = caption.TranslatePoint(new Point(), debugScroll).Y;
            Assert.True(captionTop >= headerBottom,
                "The settled regex-debug caption must appear below the expander header, without top clipping.");
            Assert.True(captionTop >= 0 && captionTop + caption.ActualHeight <= debugScroll.ActualHeight,
                "The complete introductory caption must be visible at the top of the debug workspace.");
        }
        finally
        {
            page.DataContext = originalContext;
            window.Width = originalWidth;
            PumpLayout(window, page);
        }
    }

    private static void VerifySettingsLayout(Window window, SettingsPage page)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(page));
        PumpLayout(window, page);

        var scrollViewer = Assert.IsAssignableFrom<ScrollViewer>(page.FindName("SettingsScrollViewer"));
        Assert.Equal(HorizontalAlignment.Stretch, scrollViewer.HorizontalContentAlignment);
        Assert.Equal(new Thickness(0, 0, 16, 0), scrollViewer.Padding);
        Assert.True(scrollViewer.ActualWidth > 0);

        var indexingCard = Assert.IsAssignableFrom<CardControl>(page.FindName("IndexingSettingsCard"));
        var notificationsCard = Assert.IsAssignableFrom<CardControl>(page.FindName("NotificationSettingsCard"));
        var appearanceCard = Assert.IsAssignableFrom<CardControl>(page.FindName("AppearanceSettingsCard"));
        foreach (var card in new[] { indexingCard, notificationsCard, appearanceCard })
        {
            Assert.Equal(new CornerRadius(8), card.CornerRadius);
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.NotNull(card.Background);
        }
        Assert.Equal(4, FindVisualChildren<SettingRowCard>(indexingCard).Count());
        Assert.Single(FindVisualChildren<SettingRowCard>(notificationsCard));
        Assert.Equal(4, FindVisualChildren<SettingRowCard>(appearanceCard).Count());

        var inputs = FindVisualChildren<FrameworkElement>(scrollViewer)
            .Where(static element => element is System.Windows.Controls.TextBox or ComboBox)
            .ToArray();
        Assert.NotEmpty(inputs);
        foreach (var input in inputs)
        {
            Assert.Equal(double.PositiveInfinity, input.MaxWidth);
            Assert.Equal(HorizontalAlignment.Stretch, input.HorizontalAlignment);
        }

        var textInputs = inputs.OfType<Wpf.Ui.Controls.TextBox>().ToArray();
        var batchInput = Assert.Single(textInputs, input =>
            BindingOperations.GetBinding(input, System.Windows.Controls.TextBox.TextProperty)?.Path?.Path
                == nameof(SettingsViewModel.UiUpdateBatchSizeText));
        var durationInput = Assert.Single(textInputs, input =>
            BindingOperations.GetBinding(input, System.Windows.Controls.TextBox.TextProperty)?.Path?.Path
                == nameof(SettingsViewModel.NotificationAutoDismissSecondsText));
        Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(batchInput)));
        Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(durationInput)));
        Assert.NotNull(batchInput.ToolTip);
        Assert.NotNull(durationInput.ToolTip);
        var viewModel = Assert.IsType<SettingsViewModel>(page.DataContext);
        var densityInput = Assert.Single(inputs.OfType<ComboBox>(), input =>
            BindingOperations.GetBinding(input, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.Path?.Path
                == nameof(SettingsViewModel.SelectedCatalogRowDensity));
        Assert.Same(viewModel.CatalogRowDensityChoices, densityInput.ItemsSource);
        Assert.Equal(3, densityInput.Items.Count);
        Assert.NotNull(densityInput.ToolTip);
        Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(densityInput)));
        var batch = viewModel.UiUpdateBatchSizeText;
        var duration = viewModel.NotificationAutoDismissSecondsText;
        try
        {
            batchInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "0");
            batchInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            Assert.True(viewModel.HasNumericValidationError);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            batchInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, batch);
            batchInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            durationInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "61");
            durationInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            Assert.True(viewModel.HasNumericValidationError);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
        }
        finally
        {
            batchInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, batch);
            durationInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, duration);
            batchInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            durationInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        }
        Assert.False(viewModel.HasNumericValidationError);
    }

    private static void VerifyOperationInfoBar(IServiceProvider provider, MainWindow window)
    {
        var notifications = provider.GetRequiredService<OperationNotificationService>();
        Assert.Same(notifications, provider.GetRequiredService<ISnackbarService>());
        var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(window.FindName("OperationInfoBar"));
        var navigation = Assert.IsType<NavigationView>(window.FindName("RootNavigation"));
        var overlay = Assert.IsType<Grid>(window.FindName("PageNotificationOverlay"));
        Assert.Same(overlay, navigation.ContentOverlay);
        Assert.Same(overlay, infoBar.Parent);
        Assert.Same(notifications, infoBar.DataContext);
        Assert.True(infoBar.IsClosable);
        Assert.Equal(VerticalAlignment.Bottom, infoBar.VerticalAlignment);
        Assert.Equal(Visibility.Collapsed,
            Assert.IsType<SnackbarPresenter>(window.FindName("SnackbarPresenter")).Visibility);

        var originalWidth = window.Width;
        var originalHeight = window.Height;
        try
        {
            foreach (var (width, expanded, label) in new[]
                     {
                         (1360d, true, "wide"),
                         (760d, false, "compact"),
                     })
            {
                notifications.DismissCommand.Execute(null);
                window.Width = width;
                window.Height = 640;
                // A user may keep the rail collapsed at a wide width. Set both
                // states explicitly instead of relying on a shell-mode change.
                navigation.IsPaneOpen = expanded;
                // Finish the pane's Fluent animation before taking stable bounds.
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                PumpLayout(window, window);
                Assert.Equal(expanded, navigation.IsPaneOpen);
                var presenter = Assert.IsAssignableFrom<FrameworkElement>(navigation.Template.FindName(
                    "PART_NavigationViewContentPresenter", navigation));
                var pane = Assert.IsType<Grid>(navigation.Template.FindName("PaneGrid", navigation));
                var settingsItem = Assert.Single(navigation.FooterMenuItems.OfType<NavigationViewItem>(),
                    item => item.TargetPageType == typeof(SettingsPage));
                var footer = Assert.IsAssignableFrom<FrameworkElement>(settingsItem);
                var navigationBounds = Bounds(navigation);
                var paneBounds = Bounds(pane);
                var footerBounds = Bounds(footer);
                var pageBounds = Bounds(presenter);
                Assert.Equal(0, presenter.Margin.Bottom);

                // Legacy callers cannot replace the user's configured timeout.
                // Timing itself uses a fake clock in OperationNotificationServiceTests.
                notifications.Show("UI verification", "Operation completed", ControlAppearance.Caution,
                    null, TimeSpan.FromHours(1));
                PumpLayout(window, window);
                Assert.True(notifications.IsOpen);
                Assert.True(infoBar.IsOpen);
                Assert.Equal(Visibility.Visible, infoBar.Visibility);
                Assert.Equal("UI verification", infoBar.Title);
                Assert.Equal("Operation completed", infoBar.Message);
                Assert.Equal(InfoBarSeverity.Warning, infoBar.Severity);
                Assert.True(infoBar.ActualHeight > 0);
                Assert.Equal(0, presenter.Margin.Bottom);
                Assert.Equal(HorizontalAlignment.Right, infoBar.HorizontalAlignment);
                Assert.Equal(480, infoBar.MaxWidth);
                Assert.Null(overlay.Background);
                AssertBoundsUnchanged(navigationBounds, Bounds(navigation));
                AssertBoundsUnchanged(paneBounds, Bounds(pane));
                AssertBoundsUnchanged(footerBounds, Bounds(footer));
                Assert.Equal(0, navigation.FrameMargin.Bottom);
                Assert.Equal(0, navigation.Padding.Bottom);
                var notificationBounds = Bounds(infoBar);
                var remainingPageBounds = Bounds(presenter);
                Assert.True(notificationBounds.Left >= paneBounds.Right,
                    "Notifications must never extend into the navigation pane.");
                Assert.True(notificationBounds.Left >= remainingPageBounds.Left);
                Assert.True(notificationBounds.Right <= remainingPageBounds.Right + 1);
                Assert.True(notificationBounds.Bottom <= remainingPageBounds.Bottom);
                AssertBoundsUnchanged(pageBounds, remainingPageBounds);
                SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content),
                    $"notifications-{label}-open.png");

                var dismiss = Assert.Single(FindVisualChildren<Wpf.Ui.Controls.Button>(infoBar),
                    button => ReferenceEquals(button.Command, notifications.DismissCommand));
                Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(dismiss)));
                dismiss.Command!.Execute(null);
                PumpLayout(window, window);
                Assert.False(notifications.IsOpen);
                Assert.False(infoBar.IsOpen);
                Assert.Equal(Visibility.Collapsed, infoBar.Visibility);
                Assert.Equal(0, presenter.Margin.Bottom);
                AssertBoundsUnchanged(navigationBounds, Bounds(navigation));
                AssertBoundsUnchanged(paneBounds, Bounds(pane));
                AssertBoundsUnchanged(footerBounds, Bounds(footer));
                AssertBoundsUnchanged(pageBounds, Bounds(presenter));
                SaveQaImage(Assert.IsAssignableFrom<FrameworkElement>(window.Content),
                    $"notifications-{label}-closed.png");

                notifications.Show("Dismiss", "Two-way close binding", ControlAppearance.Secondary,
                    null, TimeSpan.Zero);
                PumpLayout(window, window);
                infoBar.SetCurrentValue(Wpf.Ui.Controls.InfoBar.IsOpenProperty, false);
                infoBar.GetBindingExpression(Wpf.Ui.Controls.InfoBar.IsOpenProperty)!.UpdateSource();
                Assert.False(notifications.IsOpen);
                PumpLayout(window, window);
                Assert.Equal(Visibility.Collapsed, infoBar.Visibility);
                Assert.Equal(0, presenter.Margin.Bottom);
            }
        }
        finally
        {
            notifications.DismissCommand.Execute(null);
            window.Width = originalWidth;
            window.Height = originalHeight;
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            PumpLayout(window, window);
        }

        Rect Bounds(FrameworkElement element)
            => new(element.TranslatePoint(new Point(), window), element.RenderSize);

        static void AssertBoundsUnchanged(Rect expected, Rect actual)
        {
            Assert.InRange(Math.Abs(expected.Left - actual.Left), 0, 1);
            Assert.InRange(Math.Abs(expected.Top - actual.Top), 0, 1);
            Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, 1);
            Assert.InRange(Math.Abs(expected.Height - actual.Height), 0, 1);
        }
    }

    private static void VerifyCatalogDensity(IAppearanceService appearance, MainWindow window, CatalogPage page)
    {
        var grid = Assert.IsAssignableFrom<System.Windows.Controls.DataGrid>(page.FindName("ResultsGrid"));
        var source = grid.ItemsSource;
        var columns = grid.Columns.Count;
        foreach (var density in Enum.GetValues<CatalogRowDensity>())
        {
            appearance.Apply(window, new AppearanceOptions
            {
                Theme = ThemePreference.Light, Backdrop = BackdropPreference.None, CatalogRowDensity = density,
            });
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            var metrics = CatalogDensityMetrics.Resolve(density);
            Assert.Equal(metrics.RowHeight, grid.RowHeight);
            Assert.Same(source, grid.ItemsSource);
            Assert.Equal(columns, grid.Columns.Count);
            Assert.Equal(metrics.CellPadding, grid.FindResource("CatalogCellPadding"));
            Assert.Equal(metrics.RowMargin, grid.FindResource("CatalogRowMargin"));
        }
        appearance.Apply(window, new AppearanceOptions { Theme = ThemePreference.Light, Backdrop = BackdropPreference.None });
    }

    private static void VerifyOutputFormatEditor(IServiceProvider provider, Window window, OutputFormatsPage page)
    {
        VerifyOutputPreviewLayout(window, page);
        var scroll = Assert.IsAssignableFrom<ScrollViewer>(page.FindName("OutputFormatsScrollViewer"));
        Assert.Equal(new Thickness(0, 0, 16, 0), scroll.Padding);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([new StubLoadedProfile()], [], DateTimeOffset.UtcNow));
        // Profile publication is marshalled to the UI scheduler. Let that refresh
        // finish before creating a new editable output draft.
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
        var viewModel = Assert.IsType<OutputFormatsViewModel>(page.DataContext);
        var create = viewModel.NewFormatCommand.ExecuteAsync(null);
        PumpDispatcherUntil(window.Dispatcher, () => create.IsCompleted, TimeSpan.FromSeconds(3));
        create.GetAwaiter().GetResult();
        PumpLayout(window, page);

        var profileChoice = Assert.Single(viewModel.ProfileChoices, choice =>
            choice.ProfileId == "scan-smoke-profile");
        var profilePicker = Assert.IsType<ComboBox>(page.FindName("OutputProfileComboBox"));
        profilePicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, profileChoice);
        profilePicker.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)!.UpdateSource();
        PumpLayout(window, page);
        Assert.Same(profileChoice, viewModel.SelectedProfileChoice);
        var clientToken = Assert.Single(viewModel.ProfileTokenChoices, choice => choice.Token == "{Field:client}");
        var tokenButton = Assert.Single(FindVisualChildren<Wpf.Ui.Controls.Button>(scroll),
            button => ReferenceEquals(button.DataContext, clientToken));
        Assert.NotNull(tokenButton.ToolTip);

        var template = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("TemplateTextBox"));
        template.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "before|after");
        template.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        template.Select(6, 1);
        tokenButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("before{Field:client}after", viewModel.DraftTemplate);

        var prefix = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("OutputPrefixTextBox"));
        var suffix = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("OutputSuffixTextBox"));
        foreach (var input in new[] { prefix, suffix })
        {
            Assert.Equal(HorizontalAlignment.Stretch, input.HorizontalAlignment);
            Assert.Equal(double.PositiveInfinity, input.MaxWidth);
            Assert.NotNull(input.ToolTip);
        }
        prefix.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "[");
        suffix.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "]");
        prefix.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        suffix.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        Assert.Equal("[", viewModel.DraftPrefix);
        Assert.Equal("]", viewModel.DraftSuffix);
        Assert.True(viewModel.IsPreviewValid);
        Assert.StartsWith("[before", viewModel.PreviewText, StringComparison.Ordinal);
        Assert.EndsWith("after]", viewModel.PreviewText, StringComparison.Ordinal);
        viewModel.DiscardChanges();
    }

    private static void VerifyOutputPreviewLayout(Window window, OutputFormatsPage page)
    {
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        try
        {
            foreach (var width in new[] { 1360d, 760d })
            {
                window.Width = width;
                window.Height = 860;
                PumpLayout(window, page);
                var workspace = Assert.IsType<SplitWorkspace>(page.FindName("OutputWorkspace"));
                if (workspace.ActualWidth < 900)
                {
                    var tabs = Assert.Single(FindVisualChildren<TabControl>(workspace));
                    tabs.SelectedIndex = 1;
                    PumpLayout(window, page);
                }
                var card = Assert.IsType<SectionCard>(page.FindName("OutputPreviewCard"));
                var content = Assert.IsType<Grid>(page.FindName("OutputPreviewContent"));
                var title = Assert.IsAssignableFrom<System.Windows.Controls.TextBlock>(page.FindName("OutputPreviewTitle"));
                var preview = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("OutputPreviewTextBox"));
                Assert.True(card.ActualHeight > 180);
                Assert.InRange(title.TranslatePoint(new Point(), card).Y, 16, 18);
                Assert.True(content.ActualHeight >= card.ActualHeight - 36,
                    "The preview grid must stretch through its native card rather than remain vertically centered.");
                Assert.True(preview.ActualHeight / card.ActualHeight >= 0.60,
                    "Preview text must occupy the majority of the available preview surface.");
                Assert.True(card.TranslatePoint(new Point(0, card.ActualHeight), page).Y <= page.ActualHeight + 1,
                    "The full-height preview must remain inside the page and above its action footer.");
            }
        }
        finally
        {
            window.Width = originalWidth;
            window.Height = originalHeight;
            PumpLayout(window, page);
        }
    }

    private static void VerifyPageScrollContracts(FilesPage filesPage, CatalogPage catalogPage)
    {
        Assert.False(ScrollViewer.GetCanContentScroll(filesPage));
        Assert.False(ScrollViewer.GetCanContentScroll(catalogPage));
        var filterScrollViewer = Assert.IsAssignableFrom<ScrollViewer>(
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
                    ["Loc.LiveBrowser.Title"] = "Live folder contents",
                    ["Loc.Catalog.Column.FolderPath"] = "Folder path",
                    ["Loc.Catalog.Selection.Item"] = "Select item",
                }));
        DynamicProfileGrid.SetFields(grid, new[] { field, userInputField });
        DynamicProfileGrid.SetLayoutStore(
            grid,
            new StubGridLayoutStore(new GridColumnLayout("client", 1, 180, GridColumnWidthMode.Manual)));
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
            Assert.Equal(64, selectionColumn.Width.Value);
            Assert.Equal(64, selectionColumn.MinWidth);
            Assert.Equal(64, selectionColumn.MaxWidth);
            Assert.Equal(2, grid.FrozenColumnCount);
            var folderColumn = grid.Columns[1];
            var folderHeader = Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(folderColumn.Header);
            Assert.Equal("Live folder contents", folderHeader.ToolTip);
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.MoreHorizontal20, folderHeader.Symbol);
            Assert.Equal(1, folderColumn.DisplayIndex);
            Assert.False(folderColumn.CanUserReorder);
            Assert.False(folderColumn.CanUserSort);
            Assert.Contains(grid.Columns, column => Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "Folder path"));
            var clientColumn = Assert.Single(grid.Columns, column => Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "고객"));
            // Old saved display positions never override the profile's field order.
            Assert.Equal(2, clientColumn.DisplayIndex);
            Assert.Equal(180, clientColumn.Width.Value);
            Assert.Single(grid.Columns, column => Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "검토 메모"));
            var row = Assert.IsType<System.Windows.Controls.DataGridRow>(
                grid.ItemContainerGenerator.ContainerFromItem(item));
            var userInput = Assert.Single(FindVisualChildren<Wpf.Ui.Controls.TextBox>(row),
                editor => editor.PlaceholderText == "메모 입력");
            Assert.Equal("메모 입력", userInput.PlaceholderText);
            var selectable = FindVisualChildren<SelectableCatalogText>(row).ToArray();
            Assert.NotEmpty(selectable);
            Assert.All(selectable, content =>
            {
                Assert.True(content.TextBox.IsReadOnly);
                Assert.True(content.TextBox.IsTextSelectionEnabled);
                var textBinding = Assert.IsType<Binding>(BindingOperations.GetBindingBase(
                    content.TextBox, System.Windows.Controls.TextBox.TextProperty));
                Assert.Equal(BindingMode.OneWay, textBinding.Mode);
                Assert.False(content.TextBox.ClearButtonEnabled);
                Assert.Equal(0, content.TextBox.MinHeight);
                Assert.False(content.Highlight.IsHitTestVisible);
                Assert.NotNull(content.TextBox.ContextMenu);
                Assert.Empty(content.TextBox.ContextMenu!.Items);
                content.PrepareContextMenu();
                Assert.Equal(5, content.TextBox.ContextMenu.Items.Count);
                Assert.All(content.TextBox.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>(),
                    menu => Assert.IsType<Wpf.Ui.Controls.MenuItem>(menu));
            });
            var mappedText = Assert.Single(selectable, content => content.TextBox.Text == "아폴로");
            mappedText.TextBox.Select(1, 2);
            Assert.Equal("폴로", mappedText.TextBox.SelectedText);
            var selector = Assert.Single(
                FindVisualChildren<System.Windows.Controls.CheckBox>(row));
            var selectionCell = Assert.Single(FindVisualChildren<DataGridCell>(row),
                cell => ReferenceEquals(cell.Column, selectionColumn));
            Assert.Equal(new Thickness(12, 0, 12, 0), selectionCell.Padding);
            Assert.Equal(24, selector.MinWidth);
            var selectorLeft = selector.TranslatePoint(new Point(0, 0), selectionCell).X;
            Assert.True(selectorLeft >= 12, "The structured checkbox needs at least 12 DIP on its left.");
            Assert.True(selectionCell.ActualWidth - selectorLeft - selector.ActualWidth >= 12,
                "The structured checkbox needs at least 12 DIP on its right.");
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
            grid.DataContext = new SharedFilterContext("POL", true);
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.True(userInput.IsReadOnly);
            Assert.False(userInput.ClearButtonEnabled);
            mappedText.TextBox.Select(0, 2);
            Assert.Equal("Ap", mappedText.TextBox.SelectedText);
            mappedText.DataContext = null;
            Assert.Empty(mappedText.TextBox.SelectedText);
            // Changing profile order with the same ID keeps width preferences,
            // but not a stale saved position. Source kinds share one global order.
            DynamicProfileGrid.SetFields(grid, new[] { field with { Order = 20 }, userInputField with { Order = 10 } });
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            var reorderedClient = Assert.Single(grid.Columns, column => Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "고객"));
            var reorderedMemo = Assert.Single(grid.Columns, column => Equals(DynamicProfileGrid.GetColumnHeaderContent(column), "검토 메모"));
            Assert.Equal(2, reorderedMemo.DisplayIndex);
            Assert.Equal(3, reorderedClient.DisplayIndex);
            Assert.Equal(180, reorderedClient.Width.Value);
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

    private sealed record FilterContext(string AppliedHighlightQuery);
    private sealed record SharedFilterContext(string AppliedHighlightQuery, bool IsReadOnlyDataSource);

    private sealed class WorkspaceLayoutFixture
    {
        private readonly object _profile;
        private readonly IAppLocalizer _localizer;
        private readonly object _format = new { Id = "layout-output", DisplayName = "Full paths — quoted, comma separated" };
        public WorkspaceLayoutFixture(bool isCatalog, IAppLocalizer? localizer = null)
        {
            _localizer = localizer ?? new TestAppLocalizer();
            var english = _localizer.Culture.TwoLetterISOLanguageName == "en";
            _profile = new { Id = "layout-sample", DisplayName = english ? "Project folders" : "프로젝트 폴더" };
            SelectedProfile = _profile;
            SelectedOutputFormat = _format;
            var fields = new ProfileFieldDescriptor[]
            {
                new("client", "client", english ? "Client" : "고객", 0, true, ProfileFieldValueKind.String, false, null, null),
                new("project", "project", english ? "Project" : "프로젝트", 1, true, ProfileFieldValueKind.String, false, null, null),
                new("date", "date", english ? "Date" : "기준일", 2, false, ProfileFieldValueKind.DateTime, true, "yyyyMMdd", "yyyy-MM-dd"),
                new("note", "", english ? "Review note" : "검토 메모", 3, false, ProfileFieldValueKind.String, true, null, null) { SourceKind = ProfileFieldSourceKind.UserInput },
            };
            Fields = isCatalog ? fields : [];
            SelectedCount = isCatalog ? 100000 : 1000000;
            Items = Enumerable.Range(0, 12).Select(index =>
            {
                var name = isCatalog ? $"Project-{index + 1:00}" : $"Project-{index + 1:00}.pdf";
                var path = Path.Combine(RootPath, name);
                if (!isCatalog) return (object)new FileSearchItemViewModel(new IndexedPathEntry(path, name, RootPath,
                    IndexedPathKind.File, (index + 1) * 128000, DateTimeOffset.Parse("2026-05-21T09:00:00Z"),
                    DateTimeOffset.Parse("2026-10-13T09:00:00Z"), false), _localizer);
                return new CatalogItemViewModel(new CatalogItem(path, name, "sample", new object(),
                    new Dictionary<string, object?> { ["client"] = index % 2 == 0 ? "Apollo" : "Contoso", ["project"] = name,
                        ["date"] = new DateTime(2026, 5, 21).AddDays(index) }, false), fields, _localizer);
            }).ToArray();
        }
        public bool HasIndex => true;
        public IAppLocalizer Localizer => _localizer;
        public bool HasLoadedItems => true;
        public bool HasSelection => true;
        public bool CanSearch => true;
        public bool CanEditWorkspace => true;
        public bool IsDetailsOpen => false;
        public bool IsBusy => false;
        public bool IsSearching => false;
        public bool CanRetryResultCount { get; set; }
        public System.Windows.Input.ICommand RetryResultCountCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public bool IsSetupStateVisible => false;
        public bool IsNoResultsVisible => false;
        public bool HasValueMappings => false;
        public bool HasColumnFilters => false;
        public bool IsReadOnlyDataSource => false;
        public CatalogActiveColumnFilter[] ActiveColumnFilters => [];
        public System.Windows.Input.ICommand ClearColumnFiltersCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public WorkspaceTransferLayoutFixture WorkspaceTransfer { get; } = new();
        public bool HasRoot => true;
        public bool HasNoRoot => false;
        public bool IsCreatedCustomRange => false;
        public bool IsModifiedCustomRange => false;
        public bool UsesQuerySelection { get; set; }
        public int SelectedCount { get; }
        public string RootPath { get; set; } = @"C:\Archive\Projects";
        public string DetailsButtonText => _localizer.Get("Loc.Workspace.Details", "자세히");
        public string ResultSummary => _localizer.Format("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", 1000000);
        public string FilterSummary => _localizer.Format("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", 100000);
        public string StatusMessage => _localizer.Get("Loc.Common.Ready", "준비됨");
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
        public object[] OutputFormats => [_format];
    }

    private sealed class WorkspaceTransferLayoutFixture
    {
        public WorkspaceTransferLayoutFixture()
        {
            var localSource = new WorkspaceSourceChoice("local", Resource("Loc.Transfer.Source.Local", "로컬 스캔 결과"), null);
            SelectedSource = localSource;
            Sources = [localSource];
        }
        public bool IsBusy => false;
        public bool IsImported => false;
        public bool IsLocal => true;
        public bool CanSelectSource => true;
        public string ProgressText => string.Empty;
        public string SourceDescription => Resource("Loc.Transfer.Source.Local.Help", "이 PC에서 스캔한 결과입니다.");
        public WorkspaceSourceChoice[] Sources { get; }
        public WorkspaceSourceChoice? SelectedSource { get; set; }
        public System.Windows.Input.ICommand OpenCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public System.Windows.Input.ICommand ExportPackageCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public System.Windows.Input.ICommand ExportProfileCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public System.Windows.Input.ICommand CancelCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public System.Windows.Input.ICommand RelinkCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        public System.Windows.Input.ICommand UseLocalCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(static () => { });
        private static string Resource(string key, string fallback) => System.Windows.Application.Current.TryFindResource(key) as string ?? fallback;
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

    private sealed class LaterCatalogMissingDataPrompt : ICatalogMissingDataPrompt
    {
        public Task<CatalogMissingDataDecision> ChooseAsync(string profileName, bool hasIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CatalogMissingDataDecision.Later);
        }
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
