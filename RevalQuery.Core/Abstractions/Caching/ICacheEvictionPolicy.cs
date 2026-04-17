using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// Interface for cache eviction policies.
/// Allows pluggable eviction strategies (TTL, LRU, etc.).
/// </summary>
public interface ICacheEvictionPolicy
{
    /// <summary>
    /// Registers a key for potential eviction.
    /// </summary>
    void RegisterForEviction(ITuple key, TimeSpan gcTime);

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