using System.Runtime.CompilerServices;
using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// The rule deciding when an unobserved query leaves the registry.
/// </summary>
/// <remarks>
/// An implementation shares the lifetime of the <see cref="RevalClient"/> that uses it.
/// Registering one as a singleton alongside a scoped client leaks data between users.
/// </remarks>
public interface ICacheEvictionPolicy
{
    /// <summary>
    /// Registers a key for eviction. Called when a query is left with no observers.
    /// The policy starts its background work on the first call.
    /// </summary>
    /// <param name="key">The key of the query to track.</param>
    /// <param name="cacheOptions">The query's cache options, or null to use the client defaults.</param>
    void RegisterForEviction(ITuple key, CacheOptions? cacheOptions);

    /// <summary>
    /// Cancels pending eviction for a key. Called when a query gains its first observer.
    /// </summary>
    /// <param name="key">The key to cancel eviction for.</param>
    void CancelEviction(ITuple key);

    /// <summary>
    /// Raised when a key should leave the registry.
    /// </summary>
    event Action<ITuple>? OnEvictionRequired;

    /// <summary>
    /// Stops the background work. Called when the owning client is disposed.
    /// </summary>
    Task StopAsync();
}
