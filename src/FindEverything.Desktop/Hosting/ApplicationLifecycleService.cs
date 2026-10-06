using FindEverything.Application.Options;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wpf.Ui;

namespace FindEverything.Desktop.Hosting;

public sealed class ApplicationLifecycleService(
    IServiceProvider serviceProvider,
    IProfilePluginLoader profilePluginLoader,
    IProfileCatalogPublisher profileCatalogPublisher,
    IApplicationOperationCoordinator operationCoordinator,
    INavigationService navigationService,
    IOptions<HostLifecycleOptions> hostOptions,
    IOptions<PluginDiscoveryOptions> pluginOptions,
    IOptions<WorkspaceOptions> workspaceOptions,
    IOptions<IndexingOptions> indexingOptions,
    IOptions<AppearanceOptions> appearanceOptions,
    ILogger<ApplicationLifecycleService> logger) : IHostedService
{
    private int _stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving Value explicitly makes all startup option validation finish before
        // any window is created.
        _ = hostOptions.Value;
        _ = pluginOptions.Value;
        _ = workspaceOptions.Value;
        _ = indexingOptions.Value;
        _ = appearanceOptions.Value;

        ProfileCatalogSnapshot snapshot;
        try
        {
            snapshot = await profilePluginLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Profile discovery failed unexpectedly.");
            snapshot = new ProfileCatalogSnapshot(
                [],
                [
                    new ProfilePluginReport(
                        pluginOptions.Value.ProfilesDirectory,
                        null,
                        null,
                        ProfilePluginStatus.Disabled,
                        [
                            new ProfileDiagnostic(
                                ProfileDiagnosticSeverity.Error,
                                "plugin_discovery_failed",
                                "프로필 검색 중 예기치 않은 오류가 발생했습니다.",
                                exception.Message),
                        ]),
                ],
                DateTimeOffset.UtcNow);
        }

        profileCatalogPublisher.Publish(snapshot);

        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF Application 인스턴스를 찾을 수 없습니다.");
        await application.Dispatcher.InvokeAsync(() =>
        {
            // WPF views must be constructed on the Dispatcher. Resolve every
            // top-level graph before showing the shell so navigation cannot reveal
            // a late DI failure.
            _ = serviceProvider.GetRequiredService<CatalogPage>();
            _ = serviceProvider.GetRequiredService<ProfilesPage>();
            _ = serviceProvider.GetRequiredService<SettingsPage>();
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            application.MainWindow = mainWindow;

            void NavigateWhenLoaded(object? sender, System.Windows.RoutedEventArgs args)
            {
                mainWindow.Loaded -= NavigateWhenLoaded;
                _ = navigationService.Navigate(typeof(CatalogPage));
            }

            mainWindow.Loaded += NavigateWhenLoaded;
            mainWindow.Show();
            application.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        operationCoordinator.Cancel();
        await operationCoordinator.CancelAndWaitAsync(
            hostOptions.Value.ShutdownTimeout,
            cancellationToken).ConfigureAwait(false);
    }
}
