using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Evicts an unobserved query once its GcTime has elapsed.
/// The sweep timer is created on the first registration and stops with the owning client,
/// so there is no hosted service to start and WebAssembly needs no host.
/// </summary>
public sealed class TtlQueryGarbageCollector(RevalQueryOptions defaultOptions) : ICacheEvictionPolicy, IDisposable, IAsyncDisposable
{
    private const int MaxTrackedQueries = 10_000;
    private const int EvictionBatchSize = 1_000;

    private readonly ConcurrentDictionary<ITuple, EvictionToken> _deathRow = new(QueryKeyComparer.Instance);
    private readonly object _lifecycleGate = new();
    private ITimer? _timer;
    private TaskCompletionSource _sweepsDone = new();
    private int _runningSweeps;
    private bool _isStopped;

    /// <summary>
    /// Raised when a key should leave the registry.
    /// </summary>
    public event Action<ITuple>? OnEvictionRequired;

    internal int EvictionSubscriberCount => OnEvictionRequired?.GetInvocationList().Length ?? 0;

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
            Expiry = defaultOptions.TimeProvider.GetUtcNow().Add(gcTime)
        };

        EnsureStarted();

        if (_deathRow.Count > MaxTrackedQueries) EvictOldestEntries();
    }

    /// <summary>
    /// Cancels pending eviction for a key.
    /// </summary>
    public void CancelEviction(ITuple key)
    {
        _deathRow.TryRemove(key, out _);
    }

    /// <summary>
    /// Stops the collection loop and waits for any sweep that is running, including one that
    /// was running when <see cref="Dispose"/> stopped the timer. Further registrations will not
    /// restart it.
    /// </summary>
    public async Task StopAsync()
    {
        Task sweepsDone;

        lock (_lifecycleGate)
        {
            _isStopped = true;
            _timer?.Dispose();
            sweepsDone = _runningSweeps is 0 ? Task.CompletedTask : _sweepsDone.Task;
        }

        await sweepsDone;
    }

    /// <summary>
    /// Stops the collection loop and waits for it to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Stops the collection loop without waiting for a running sweep. Present so a container
    /// disposing its scope synchronously, which is the common case, does not have to.
    /// <see cref="StopAsync"/> still waits for that sweep afterwards.
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            _isStopped = true;
            _timer?.Dispose();
        }
    }

    /// <summary>
    /// Evicts every entry whose expiry has passed. Public for tests and admin scenarios;
    /// the sweep timer calls it on each interval.
    /// </summary>
    public void CollectExpiredEntries()
    {
        var now = defaultOptions.TimeProvider.GetUtcNow();
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
        if (_timer is not null) return;

        lock (_lifecycleGate)
        {
            if (_timer is not null || _isStopped) return;

            var interval = defaultOptions.CacheOptions.GcInterval;
            _timer = defaultOptions.TimeProvider.CreateTimer(_ => Sweep(), null, interval, interval);
        }
    }

    private void Sweep()
    {
        lock (_lifecycleGate)
        {
            if (_isStopped) return;
            if (_runningSweeps++ is 0) _sweepsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            CollectExpiredEntries();
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (--_runningSweeps is 0) _sweepsDone.TrySetResult();
            }
        }
    }

    private void EvictOldestEntries()
    {
        var toEvict = _deathRow
            .OrderBy(x => x.Value.Expiry)
            .Take(EvictionBatchSize)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in toEvict)
            if (_deathRow.TryRemove(key, out var token))
                OnEvictionRequired?.Invoke(token.Key);
    }
}
