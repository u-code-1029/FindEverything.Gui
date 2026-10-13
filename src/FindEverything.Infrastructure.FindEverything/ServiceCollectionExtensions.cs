using FindEverything.Application.Indexing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FindEverything.Infrastructure.FindEverything;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFindEverythingInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<FindEverythingIndexSessionFactory>();
        services.TryAddSingleton<ILocalIndexSessionFactory>(
            static provider => provider.GetRequiredService<FindEverythingIndexSessionFactory>());
        services.TryAddSingleton<IIndexSessionFactory>(
            static provider => provider.GetRequiredService<ILocalIndexSessionFactory>());
        services.TryAddSingleton<IDirectoryDiscoveryService, DirectoryDiscoveryService>();
        return services;
    }
}
