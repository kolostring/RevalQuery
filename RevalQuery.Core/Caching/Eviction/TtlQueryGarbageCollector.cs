using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Key;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Time-to-live based garbage collection policy for cached queries.
/// Runs in background, evicts entries after their GcTime expires.
/// </summary>
public sealed class TtlQueryGarbageCollector(RevalQueryOptions defaultOptions) : ICacheEvictionPolicy, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, EvictionToken> _deathRow = new();
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task _collectionTask = Task.CompletedTask;

    /// <summary>
    /// Raised when entries should be evicted from cache.
    /// </summary>
    public event Action<ITuple>? OnEvictionRequired;

    /// <summary>
    /// Registers a query state for potential eviction when last subscriber leaves.
    /// </summary>
    public void RegisterForEviction<TKey, TResponse>(QueryState<TKey, TResponse> queryState) where TKey : ITuple
    {
        var hashCode = CacheKeyCalculator.GetHashCode(queryState.Key);
        var token = new EvictionToken
        {
            Key = queryState.Key,
            KeyHashCode = hashCode,
            Expiry = DateTime.UtcNow.Add(EnsureCacheOptions(queryState.CacheOptions).GcTime)
        };

        _deathRow[hashCode] = token;

        if (_deathRow.Count > 10000) CleanupOldestEntries();
    }

    /// <summary>
    /// Cancels pending eviction when a new subscriber is added.
    /// </summary>
    public void CancelEviction(ITuple key)
    {
        var hashCode = CacheKeyCalculator.GetHashCode(key);
        _deathRow.TryRemove(hashCode, out _);
    }

    /// <summary>
    /// Starts the background collection loop.
    /// Call once at application startup.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _collectionTask = RunCollectionLoopAsync(_cancellationTokenSource.Token);
        await Task.Yield();
    }

    /// <summary>
    /// Stops the background collection loop.
    /// Call during application shutdown.
    /// </summary>
    public async Task StopAsync()
    {
        await _cancellationTokenSource.CancelAsync();
        try
        {
            await _collectionTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// Disposes the garbage collector.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellationTokenSource.Dispose();
    }

    private async Task RunCollectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            try
            {
                await Task.Delay(defaultOptions.CacheOptions.GcInterval, ct);
                CollectExpiredEntries();
            }
            catch (OperationCanceledException)
            {
                break;
            }
    }

    /// <summary>
    /// Manually triggers collection of expired entries.
    /// For testing or admin scenarios.
    /// </summary>
    public void CollectExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expired = _deathRow
            .Where(x => x.Value.Expiry <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var hashCode in expired)
            if (_deathRow.TryRemove(hashCode, out var token))
                OnEvictionRequired?.Invoke(token.Key);
    }

    private void CleanupOldestEntries()
    {
        var toRemove = _deathRow
            .OrderBy(x => x.Value.Expiry)
            .Take(1000)
            .Select(x => x.Key)
            .ToList();

        foreach (var hashCode in toRemove) _deathRow.TryRemove(hashCode, out _);
    }

    private CoreCacheOptions EnsureCacheOptions(CacheOptions? cacheOptions)
    {
        return defaultOptions.CacheOptions.Apply(cacheOptions);
    }
}