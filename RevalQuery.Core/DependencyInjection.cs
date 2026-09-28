using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;

namespace RevalQuery.Core;

/// <summary>
/// Extension methods for registering RevalQuery services in dependency injection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers RevalQuery services in the service collection.
    /// </summary>
    /// <remarks>
    /// The eviction policy is scoped, not singleton: it shares its registry's lifetime, and one
    /// registry per user session is what keeps one user's data out of another's.
    /// Register an <see cref="IQueryPersistence"/> of your own to have queries loaded from and
    /// saved to a durable store.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRevalQuery(
        this IServiceCollection services,
        Action<RevalQueryOptions>? configure = null
    )
    {
        var options = new RevalQueryOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddScoped<ICacheEvictionPolicy, TtlQueryGarbageCollector>();
        services.AddScoped(sp => new QueryClient(
            sp,
            sp.GetRequiredService<RevalQueryOptions>(),
            sp.GetRequiredService<ICacheEvictionPolicy>(),
            sp.GetService<IQueryPersistence>()
        ));

        return services;
    }
}
