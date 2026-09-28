using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Evicts an unobserved query once its GcTime has elapsed.
/// The collection loop starts on the first registration and stops with the owning client,
/// so there is no hosted service to start and WebAssembly needs no host.
/// </summary>
public sealed class TtlQueryGarbageCollector(RevalQueryOptions defaultOptions) : ICacheEvictionPolicy, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<ITuple, EvictionToken> _deathRow = new(QueryKeyComparer.Instance);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _collectionTask = Task.CompletedTask;
    private bool _isStopped;

    /// <summary>
    /// Raised when a key should leave the registry.
    /// </summary>
    public event Action<ITuple>? OnEvictionRequired;

    /// <summary>
    /// Registers a key for eviction once its GcTime elapses, starting the collection loop
    /// if this is the first registration.
    /// </summary>
    public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions)
    {
        var gcTime = defaultOptions.CacheOptions.Apply(cacheOptions).GcTime;

        _deathRow[key] = new EvictionToken
        {
            Key = key,
            Expiry = DateTime.UtcNow.Add(gcTime)
        };

        EnsureStarted();

        if (_deathRow.Count > 10000) CleanupOldestEntries();
    }

    /// <summary>
    /// Cancels pending eviction for a key.
    /// </summary>
    public void CancelEviction(ITuple key)
    {
        _deathRow.TryRemove(key, out _);
    }

    /// <summary>
    /// Stops the collection loop. Further registrations will not restart it.
    /// </summary>
    public async Task StopAsync()
    {
        Task collectionTask;
        CancellationTokenSource? cts;

        lock (_lifecycleGate)
        {
            if (_isStopped) return;
            _isStopped = true;
            cts = _cancellationTokenSource;
            collectionTask = _collectionTask;
        }

        if (cts is null) return;

        await cts.CancelAsync();
        try
        {
            await collectionTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        cts.Dispose();
    }

    /// <summary>
    /// Stops the collection loop and waits for it to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Stops the collection loop without waiting for it to finish. Present so a container
    /// disposing its scope synchronously, which is the common case, does not have to.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;

        lock (_lifecycleGate)
        {
            if (_isStopped) return;
            _isStopped = true;
            cts = _cancellationTokenSource;
        }

        // Cancelled, not disposed: the loop may still be sitting on this token.
        cts?.Cancel();
    }

    /// <summary>
    /// Evicts every entry whose expiry has passed. Public for tests and admin scenarios;
    /// the collection loop calls it on each interval.
    /// </summary>
    public void CollectExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expired = _deathRow
            .Where(x => x.Value.Expiry <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expired)
            if (_deathRow.TryRemove(key, out var token))
                OnEvictionRequired?.Invoke(token.Key);
    }

    private void EnsureStarted()
    {
        if (_cancellationTokenSource is not null) return;

        lock (_lifecycleGate)
        {
            if (_cancellationTokenSource is not null || _isStopped) return;

            _cancellationTokenSource = new CancellationTokenSource();
            _collectionTask = Task.Run(() => RunCollectionLoopAsync(_cancellationTokenSource.Token));
        }
    }

    private async Task RunCollectionLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(defaultOptions.CacheOptions.GcInterval, ct);
                CollectExpiredEntries();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private void CleanupOldestEntries()
    {
        var toRemove = _deathRow
            .OrderBy(x => x.Value.Expiry)
            .Take(1000)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in toRemove) _deathRow.TryRemove(key, out _);
    }
}
