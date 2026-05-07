using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// Interface for cache eviction policies.
/// Allows pluggable eviction strategies.
/// </summary>
public interface ICacheEvictionPolicy
{
    /// <summary>
    /// Registers a query state for potential eviction.
    /// Called when the last subscriber is removed.
    /// </summary>
    /// <typeparam name="TKey">The query key type.</typeparam>
    /// <typeparam name="TResponse">The query response type.</typeparam>
    /// <param name="queryState">The query state to track for eviction.</param>
    void RegisterForEviction<TKey, TResponse>(QueryState<TKey, TResponse> queryState) where TKey : ITuple;

    /// <summary>
    /// Cancels pending eviction for a key.
    /// Called when a new subscriber is added.
    /// </summary>
    /// <param name="key">The key to cancel eviction for.</param>
    void CancelEviction(ITuple key);

    /// <summary>
    /// Raised when a key should be evicted from cache.
    /// Subscribe to handle actual cache removal.
    /// </summary>
    event Action<ITuple>? OnEvictionRequired;

    /// <summary>
    /// Starts the eviction policy background work.
    /// Must be called once at application startup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the eviction policy background work.
    /// Called during application shutdown.
    /// </summary>
    Task StopAsync();
}