using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Persistence;
using RevalQuery.Core.Hooks;

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
    /// saved to a durable store. Several may be registered: a load takes the first that has the
    /// key, so registration order is preference order, and a save goes to all of them.
    /// A <see cref="RevalHooks"/> is registered as transient, so every injection gets its own.
    /// It is not disposable, so the container never holds one: its lifetime is the handle from
    /// its <c>Attach</c>.
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
        services.AddScoped(sp => new RevalClient(
            sp,
            sp.GetRequiredService<RevalQueryOptions>(),
            sp.GetRequiredService<ICacheEvictionPolicy>(),
            CompositeQueryPersistence.From(sp.GetServices<IQueryPersistence>())
        ));

        services.AddTransient(sp => sp.GetRequiredService<RevalClient>().CreateHooks());

        return services;
    }
}
