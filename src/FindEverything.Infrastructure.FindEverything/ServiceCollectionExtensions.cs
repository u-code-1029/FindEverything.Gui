using FindEverything.Application.Indexing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FindEverything.Infrastructure.FindEverything;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFindEverythingInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIndexSessionFactory, FindEverythingIndexSessionFactory>();
        services.TryAddSingleton<IDirectoryDiscoveryService, DirectoryDiscoveryService>();
        return services;
    }
}
