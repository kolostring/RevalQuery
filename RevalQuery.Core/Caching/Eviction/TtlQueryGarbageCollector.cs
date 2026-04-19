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
/// Evicts entries after their configured cache lifetime expires.
/// </summary>
public sealed class TtlQueryGarbageCollector(RevalQueryOptions defaultOptions) : ICacheEvictionPolicy, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, EvictionToken> _deathRow = new();
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task _collectionTask = Task.CompletedTask;

    public event Action<ITuple>? OnEvictionRequired;

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

        // Bounded cleanup: if deathRow grows too large, remove oldest entries
        if (_deathRow.Count > 10000) CleanupOldestEntries();
    }

    public void CancelEviction(ITuple key)
    {
        var hashCode = CacheKeyCalculator.GetHashCode(key);
        _deathRow.TryRemove(hashCode, out _);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _collectionTask = RunCollectionLoopAsync(_cancellationTokenSource.Token);
        await Task.Yield();
    }

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

    private void CollectExpiredEntries()
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