using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// Interface for cache eviction policies.
/// Allows pluggable eviction strategies (TTL, LRU, etc.).
/// </summary>
public interface ICacheEvictionPolicy
{
    /// <summary>
    /// Registers a state for potential eviction.
    /// </summary>
    void RegisterForEviction<TKey, TResponse>(QueryState<TKey, TResponse> queryState) where TKey : ITuple;

    /// <summary>
    /// Cancels pending eviction for a key.
    /// </summary>
    void CancelEviction(ITuple key);

    /// <summary>
    /// Raised when a key should be evicted from cache.
    /// </summary>
    event Action<ITuple>? OnEvictionRequired;

    /// <summary>
    /// Starts the eviction policy background work.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the eviction policy background work.
    /// </summary>
    Task StopAsync();
}