using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.TestKit;

/// <summary>
/// An eviction policy that evicts only when the test says so. It records what the client
/// registers and cancels, and runs no background work, so a test evicts at an exact point
/// instead of waiting for a garbage-collection sweep.
/// </summary>
public sealed class ManualEviction : ICacheEvictionPolicy
{
    private readonly ConcurrentQueue<ITuple> _registered = new();
    private readonly ConcurrentQueue<ITuple> _cancelled = new();

    /// <summary>
    /// Every key registered for eviction, in order.
    /// </summary>
    public IReadOnlyCollection<ITuple> Registered => _registered;

    /// <summary>
    /// Every key whose eviction was cancelled, in order.
    /// </summary>
    public IReadOnlyCollection<ITuple> Cancelled => _cancelled;

    /// <inheritdoc />
    public event Action<ITuple>? OnEvictionRequired;

    /// <inheritdoc />
    public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) => _registered.Enqueue(key);

    /// <inheritdoc />
    public void CancelEviction(ITuple key) => _cancelled.Enqueue(key);

    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Asks the client to evict <paramref name="key"/>, whether or not it was registered. The
    /// client handles the request before this returns, on the calling thread.
    /// </summary>
    /// <param name="key">The key to evict.</param>
    public void Evict(ITuple key) => OnEvictionRequired?.Invoke(key);
}
