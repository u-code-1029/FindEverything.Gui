using FindEverything.Application.Catalog;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace FindEverything.Desktop;

public static class DependencyInjection
{
    public static IServiceCollection AddDesktopPresentation(
        this IServiceCollection services,
        IConfiguration configuration,
        AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);

        services.AddOptions<HostLifecycleOptions>()
            .Bind(configuration.GetSection(HostLifecycleOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PluginDiscoveryOptions>()
            .Bind(configuration.GetSection(PluginDiscoveryOptions.SectionName))
            .PostConfigure(options =>
            {
                if (!Path.IsPathRooted(options.ProfilesDirectory))
                {
                    options.ProfilesDirectory = Path.GetFullPath(
                        Path.Combine(AppContext.BaseDirectory, options.ProfilesDirectory));
                }

                options.UserProfilesDirectory = paths.UserProfilesDirectory;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddNavigationViewPageProvider();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();

        services.AddSingleton<IApplicationOperationCoordinator, ApplicationOperationCoordinator>();
        services.AddSingleton<IWorkspaceContext, WorkspaceContext>();
        services.AddSingleton<IDesktopPickerService, DesktopPickerService>();
        services.AddSingleton<IPathLauncher, ExplorerPathLauncher>();
        services.AddSingleton<IProfileAuthoringService, ProfileAuthoringService>();
        services.TryAddSingleton<IWindowsMappedDrivePathResolver, WindowsMappedDrivePathResolver>();
        services.Replace(ServiceDescriptor.Singleton<IProfilePathCanonicalizer,
            WindowsProfilePathCanonicalizer>());
        services.AddSingleton<IAppearanceService, AppearanceService>();
        services.AddSingleton<IUserSettingsWriter, AtomicUserSettingsWriter>();
        services.AddSingleton<IGridLayoutStore, GridLayoutStore>();
        services.AddSingleton<WindowsAppNotificationService>();
        services.AddSingleton<IWindowsAppNotificationSink>(
            static provider => provider.GetRequiredService<WindowsAppNotificationService>());
        services.AddSingleton<ITaskbarAttentionService, WindowsTaskbarAttentionService>();
        services.AddSingleton<IScanCompletionNotifier, WindowsScanCompletionNotifier>();
        services.AddHostedService(
            static provider => provider.GetRequiredService<WindowsAppNotificationService>());
        services.AddSingleton<SettingsReloadDiagnostics>();
        services.AddSingleton<ISettingsReloadDiagnostics>(
            static provider => provider.GetRequiredService<SettingsReloadDiagnostics>());
        services.AddHostedService(
            static provider => provider.GetRequiredService<SettingsReloadDiagnostics>());

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ScanConsoleViewModel>();
        services.AddSingleton<IScanConsolePanelController>(
            static provider => provider.GetRequiredService<ScanConsoleViewModel>());
        services.Replace(ServiceDescriptor.Singleton<ICatalogScanTraceSink>(
            static provider => provider.GetRequiredService<ScanConsoleViewModel>()));
        services.AddSingleton<FilesPage>();
        services.AddSingleton<FileSearchViewModel>();
        services.AddSingleton<CatalogPage>();
        services.AddSingleton<CatalogViewModel>();
        services.AddSingleton<ProfilesPage>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();

        services.AddHostedService<ApplicationLifecycleService>();
        return services;
    }
}
