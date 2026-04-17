using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Caching.Storage;
using RevalQuery.Core.Configuration;

namespace RevalQuery.Core;

/// <summary>
/// Extension methods for registering RevalQuery services in dependency injection.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddRevalQuery(
        this IServiceCollection services,
        Action<RevalQueryOptions>? configure = null
    )
    {
        var options = new RevalQueryOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<ICacheStorage, TrieCacheStorage>();
        services.AddSingleton<ICacheEvictionPolicy, TtlQueryGarbageCollector>();
        services.AddScoped<QueryClient>();

        return services;
    }
}