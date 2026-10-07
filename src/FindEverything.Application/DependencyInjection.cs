using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Options;
using FindEverything.Application.Profiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FindEverything.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddFindEverythingApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<IndexingOptions>()
            .Bind(configuration.GetSection(IndexingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AppearanceOptions>()
            .Bind(configuration.GetSection(AppearanceOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => Enum.IsDefined(options.Theme) && Enum.IsDefined(options.Backdrop),
                "Appearance theme and backdrop values must be supported enum values.")
            .ValidateOnStart();

        services.AddSingleton<ValidatedSettingsState<WorkspaceOptions>>();
        services.AddSingleton<IValidatedSettingsState<WorkspaceOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<WorkspaceOptions>>());
        services.AddSingleton<IValidatedSettingsUpdater<WorkspaceOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<WorkspaceOptions>>());
        services.AddSingleton<ValidatedSettingsState<IndexingOptions>>();
        services.AddSingleton<IValidatedSettingsState<IndexingOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<IndexingOptions>>());
        services.AddSingleton<IValidatedSettingsUpdater<IndexingOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<IndexingOptions>>());
        services.AddSingleton<ValidatedSettingsState<AppearanceOptions>>();
        services.AddSingleton<IValidatedSettingsState<AppearanceOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<AppearanceOptions>>());
        services.AddSingleton<IValidatedSettingsUpdater<AppearanceOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<AppearanceOptions>>());
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddSingleton<IProfilePathTemplateCompiler, ProfilePathTemplateCompiler>();

        return services;
    }
}
