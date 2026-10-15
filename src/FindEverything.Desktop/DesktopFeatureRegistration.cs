using FindEverything.Application.Catalog;
using FindEverything.Application.Profiles;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace FindEverything.Desktop;

/// <summary>Feature composition. Registration does not construct pages or start IO.</summary>
internal static class DesktopFeatureRegistration
{
    public static void AddWorkspacePersistence(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(_ => new SqliteCatalogProjectionStore(paths.ResultsDatabaseFile));
        services.AddSingleton<ICatalogProjectionStore>(provider => provider.GetRequiredService<SqliteCatalogProjectionStore>());
        services.AddSingleton<IUserInputStore>(provider => provider.GetRequiredService<SqliteCatalogProjectionStore>());
    }

    public static void AddProfileAuthoringFeature(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton<IProfileManifestRepository>(provider => new JsonProfileManifestRepository(
            paths.UserProfilesDirectory, provider.GetRequiredService<ILogger<JsonProfileManifestRepository>>()));
        services.AddSingleton<IProfileAuthoringWorkflow, ProfileAuthoringWorkflow>();
        services.AddSingleton<IProfileSampleEvaluator, ProfileSampleEvaluator>();
        services.AddSingleton<IProfileAuthoringService>(static provider => new ProfileAuthoringService(
            provider.GetRequiredService<IProfileAuthoringWorkflow>()));
        services.AddSingleton<ProfileTransferService>();
        services.AddSingleton<IProfileTransferService>(provider => provider.GetRequiredService<ProfileTransferService>());
        services.AddSingleton<IndexedDatasetTransferService>();
        services.AddSingleton<IIndexedDatasetTransferService>(provider => provider.GetRequiredService<IndexedDatasetTransferService>());
        services.AddSingleton<IImportedWorkspaceRegistry>(provider => provider.GetRequiredService<IndexedDatasetTransferService>());
        services.AddSingleton<WorkspaceTransferViewModel>();
        services.AddSingleton<IProfilePlaygroundEvaluator, ProfilePlaygroundEvaluator>();
        services.AddSingleton<IProfilePlaygroundNavigator>(static provider => new ProfilePlaygroundNavigator(
            provider.GetRequiredService<INavigationService>(), provider.GetRequiredService<ProfilePlaygroundViewModel>));
        services.AddSingleton<IProfileEditorNavigator>(static provider => new ProfileEditorNavigator(
            provider.GetRequiredService<INavigationService>(), provider.GetRequiredService<ProfilesViewModel>));
    }

    public static void AddSelectionOutputFeature(this IServiceCollection services)
    {
        services.AddSingleton<OutputPreviewSampleStore>();
        services.AddSingleton<ISelectionOutputFormatStore, SelectionOutputFormatStore>();
        services.AddSingleton<IProfileUserInputValueStore, ProfileUserInputValueStore>();
        services.AddSingleton<ISelectionOutputFormatter, SelectionOutputFormatter>();
        services.AddSingleton<ICatalogSelectionExporter, CatalogSelectionExporter>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
    }

    public static void AddFeaturePages(this IServiceCollection services)
    {
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<FilesPage>();
        services.AddSingleton<FileSearchViewModel>(provider =>
        {
            var viewModel = ActivatorUtilities.CreateInstance<FileSearchViewModel>(provider);
            viewModel.AttachWorkspaceTransfer(provider.GetRequiredService<WorkspaceTransferViewModel>());
            return viewModel;
        });
        services.AddSingleton<CatalogPage>();
        services.AddSingleton<CatalogViewModel>(provider =>
        {
            var viewModel = ActivatorUtilities.CreateInstance<CatalogViewModel>(provider);
            viewModel.AttachWorkspaceTransfer(provider.GetRequiredService<WorkspaceTransferViewModel>(),
                provider.GetRequiredService<IIndexedDatasetTransferService>());
            return viewModel;
        });
        services.AddSingleton<ProfilesPage>();
        services.AddSingleton<ProfilesViewModel>(provider =>
        {
            var viewModel = ActivatorUtilities.CreateInstance<ProfilesViewModel>(provider);
            viewModel.AttachWorkspaceTransfer(provider.GetRequiredService<WorkspaceTransferViewModel>());
            return viewModel;
        });
        services.AddSingleton<ProfilePlaygroundPage>();
        services.AddSingleton<ProfilePlaygroundViewModel>();
        services.AddSingleton<OutputFormatsPage>();
        services.AddSingleton<OutputFormatsViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();
    }
}
