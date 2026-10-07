using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FindEverything.Profile.Runtime;

public static class DependencyInjection
{
    public static IServiceCollection AddProfileRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOptions<PluginDiscoveryOptions>()
            .ValidateDataAnnotations()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.ProfilesDirectory),
                "ProfilesDirectory is required.")
            .Validate(
                static options => options.ContractMajor == ProfileContract.CurrentMajor,
                $"ContractMajor must be {ProfileContract.CurrentMajor}.")
            .ValidateOnStart();

        services.TryAddSingleton<ProfileManifestReader>();
        services.TryAddSingleton<ProfileModelCompiler>();
        services.TryAddSingleton<AbsoluteProfilePathCanonicalizer>();
        services.TryAddSingleton<IProfilePathCanonicalizer>(
            static provider => provider.GetRequiredService<AbsoluteProfilePathCanonicalizer>());
        services.TryAddSingleton<IProfileDefinitionCompiler, ProfileDefinitionCompiler>();
        services.TryAddSingleton<ProfileCatalog>();
        services.TryAddSingleton<IProfileCatalog>(
            static provider => provider.GetRequiredService<ProfileCatalog>());
        services.TryAddSingleton<IProfileCatalogPublisher>(
            static provider => provider.GetRequiredService<ProfileCatalog>());
        services.TryAddSingleton<IProfileResolver, ProfileResolver>();
        services.TryAddSingleton<IProfilePluginLoader, ProfilePluginLoader>();

        return services;
    }
}
