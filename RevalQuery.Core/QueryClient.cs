using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Caching.Key;
using RevalQuery.Core.Caching.Storage;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core;

/// <summary>
/// Main entry point for query management.
/// Coordinates state management, caching, and worker orchestration.
/// </summary>
public sealed class QueryClient
{
    private readonly Dictionary<int, IQueryState> _stateLookup = new();
    private readonly Dictionary<int, IDisposable> _workerLookup = new();
    private readonly ICacheStorage _cacheStorage;
    private readonly ICacheEvictionPolicy _evictionPolicy;
    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _defaultOptions;

    public QueryClient(
        IServiceProvider serviceProvider,
        RevalQueryOptions defaultOptions,
        ICacheStorage? cacheStorage = null,
        ICacheEvictionPolicy? evictionPolicy = null
    )
    {
        _serviceProvider = serviceProvider;
        _cacheStorage = cacheStorage ?? new TrieCacheStorage();
        _evictionPolicy = evictionPolicy ?? new TtlQueryGarbageCollector(defaultOptions);
        _evictionPolicy.OnEvictionRequired += HandleEviction;
        _defaultOptions = defaultOptions;
    }

    public QueryState<TKey, TRes> GetOrCreateQuery<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions
    ) where TKey : ITuple
    {
        var keySegments = queryOptions.Key;
        var handler = queryOptions.Handler;
        var fetchOptions = queryOptions.FetchOptions;
        var retryOptions = queryOptions.RetryOptions;
        var cacheOptions = queryOptions.CacheOptions;

        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        var state = _stateLookup.GetValueOrDefault(lookupKey);
        if (state != null)
        {
            if (state is QueryState<TKey, TRes> cachedState) return cachedState;

            throw new InvalidOperationException(
                $"Key collision at {string.Join("/", keySegments)}. " +
                $"Expected {typeof(TRes).Name} but found {state.GetType().GenericTypeArguments[0].Name}.");
        }

        _cacheStorage.GetOrCreateNode(keySegments);

        var newState = new QueryState<TKey, TRes>(
            keySegments,
            handler,
            fetchOptions,
            retryOptions,
            cacheOptions
        );
        _stateLookup[lookupKey] = newState;

        WireQueryStateWithEvictionPolicy(newState);

        return newState;
    }

    public void Invalidate(ITuple keySegments)
    {
        var node = _cacheStorage.PeekNode(keySegments);
        if (node != null) NotifyInvalidationRecursive(node);
    }

    public void Invalidate(string key) => Invalidate(ValueTuple.Create(key));

    public void Cancel(ITuple keySegments)
    {
        FindQuery(keySegments)?.Cancel();
    }

    public void Cancel(string key) => Cancel(ValueTuple.Create(key));

    public IQueryState? FindQuery(ITuple keySegments)
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        return _stateLookup.GetValueOrDefault(lookupKey);
    }

    public IQueryState? FindQuery(string key) => FindQuery(ValueTuple.Create(key));

    public ICollection<IQueryState> FindQueries(ITuple keySegments)
    {
        var node = _cacheStorage.PeekNode(keySegments);
        if (node == null) return [];

        var childNodes = _cacheStorage.GetChildNodes(node);

        return childNodes.Select(child => _stateLookup.GetValueOrDefault(child.KeyHashCode))
            .OfType<IQueryState>()
            .ToList();
    }

    public ICollection<IQueryState> FindQueries(string key) => FindQueries(ValueTuple.Create(key));

    public QueryObserver<TRes> Subscribe<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions, Action onStateHasChanged)
        where TKey : ITuple
    {
        _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var state = GetOrCreateQuery(
            queryOptions
        );

        var observer = new QueryObserver<TRes>(
            state,
            onStateHasChanged
        );

        EnsureWorkerIsRunning(state);

        return observer;
    }

    private void EnsureWorkerIsRunning<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(state.Key);

        if (_workerLookup.TryGetValue(lookupKey, out var worker))
        {
            ((QueryWorker<TKey, TRes>)worker).RunIfStale();
            return;
        }

        var newWorker = new QueryWorker<TKey, TRes>(
            _defaultOptions,
            _serviceProvider,
            state,
            null
        );

        _workerLookup[lookupKey] = newWorker;

        state.OnLastSubscriberRemoved += (_) =>
        {
            if (_workerLookup.Remove(lookupKey, out var removedWorker)) removedWorker.Dispose();
        };
        newWorker.RunIfStale();
    }

    private void HandleEviction(ITuple key)
    {
        var hash = CacheKeyCalculator.GetHashCode(key);
        _stateLookup.Remove(hash);
        var nodeFoundAndDeleted = _cacheStorage.PruneNode(key);

        if (!nodeFoundAndDeleted) throw new InvalidOperationException($"Couldn't delete node with key {key}");
    }

    private void NotifyInvalidationRecursive(CacheNode node)
    {
        var state = _stateLookup.GetValueOrDefault(node.KeyHashCode);
        state?.NotifyInvalidated();

        foreach (var child in node.Children.Values) NotifyInvalidationRecursive(child);
    }

    private void WireQueryStateWithEvictionPolicy<TKey, TResponse>(QueryState<TKey, TResponse> stateToWire)
        where TKey : ITuple
    {
        stateToWire.OnFirstSubscriberAdded += key => _evictionPolicy.CancelEviction(key);
        stateToWire.OnLastSubscriberRemoved +=
            state => _evictionPolicy.RegisterForEviction(state);
    }
}