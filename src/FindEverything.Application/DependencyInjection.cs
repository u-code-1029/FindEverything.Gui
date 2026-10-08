using FindEverything.Application.Catalog;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Options;
using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddOptions<LocalizationOptions>()
            .Bind(configuration.GetSection(LocalizationOptions.SectionName))
            .Validate(
                static options => LocalizationOptions.IsSupported(options.CultureName),
                "Localization culture must be ko-KR or en-US.")
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
        services.AddSingleton<ValidatedSettingsState<LocalizationOptions>>();
        services.AddSingleton<IValidatedSettingsState<LocalizationOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<LocalizationOptions>>());
        services.AddSingleton<IValidatedSettingsUpdater<LocalizationOptions>>(
            static provider => provider.GetRequiredService<ValidatedSettingsState<LocalizationOptions>>());
        services.TryAddSingleton<AbsoluteProfilePathCanonicalizer>();
        services.TryAddSingleton<IProfilePathCanonicalizer>(
            static provider => provider.GetRequiredService<AbsoluteProfilePathCanonicalizer>());
        services.AddSingleton<ICatalogService, CatalogService>();
        services.TryAddSingleton<ICatalogScanTraceSink, NullCatalogScanTraceSink>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddSingleton<IProfilePathTemplateCompiler, ProfilePathTemplateCompiler>();

        return services;
    }
}
