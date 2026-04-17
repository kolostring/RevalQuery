using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Caching.Key;
using RevalQuery.Core.Caching.Storage;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
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
        _evictionPolicy = evictionPolicy ?? new TtlQueryGarbageCollector();
        _evictionPolicy.OnEvictionRequired += HandleEviction;
        _defaultOptions = defaultOptions;
    }

    public QueryState<TKey, TRes> GetOrCreateQuery<TKey, TRes>(
        TKey keySegments,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler,
        FetchOptions? fetchOptions,
        CacheOptions? cacheOptions
    ) where TKey : ITuple
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        var state = _stateLookup.GetValueOrDefault(lookupKey);
        if (state != null)
        {
            if (state is QueryState<TKey, TRes> cachedState)
            {
                if (cacheOptions != null) cachedState.SetCacheOptions(cacheOptions);
                return cachedState;
            }

            throw new InvalidOperationException(
                $"Key collision at {string.Join("/", keySegments)}. " +
                $"Expected {typeof(TRes).Name} but found {state.GetType().GenericTypeArguments[0].Name}.");
        }

        _cacheStorage.GetOrCreateNode(keySegments);

        var sanitizedFetchOptions = (fetchOptions ?? new FetchOptions()).PatchNullFields(_defaultOptions.FetchOptions);

        var newState = new QueryState<TKey, TRes>(
            keySegments,
            handler,
            cacheOptions ?? _defaultOptions.CacheOptions,
            sanitizedFetchOptions
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

    public void Invalidate(string key)
    {
        var node = _cacheStorage.PeekNode(key);
        if (node != null) NotifyInvalidationRecursive(node);
    }

    public IQueryState? FindQuery(ITuple keySegments)
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        return _stateLookup.GetValueOrDefault(lookupKey);
    }

    public ICollection<IQueryState> FindQueries(ITuple keySegments)
    {
        var node = _cacheStorage.PeekNode(keySegments);
        if (node == null) return [];

        var childNodes = _cacheStorage.GetChildNodes(node);

        return childNodes.Select(child => _stateLookup.GetValueOrDefault(child.KeyHashCode))
            .OfType<IQueryState>()
            .ToList();
    }

    public QueryObserver Subscribe<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions, Action onStateHasChanged)
        where TKey : ITuple
    {
        _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var prerenderState = new QueryState<TKey, TRes>(
            queryOptions.Key,
            queryOptions.Handler,
            _defaultOptions.CacheOptions,
            _defaultOptions.FetchOptions
        );

        var state = GetOrCreateQuery(
            queryOptions.Key,
            queryOptions.Handler,
            queryOptions.FetchOptions,
            queryOptions.CacheOptions ?? new CacheOptions(TimeSpan.Zero)
        ) ?? prerenderState;

        var observer = new QueryObserver(
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
            _serviceProvider,
            state,
            null
        );

        _workerLookup[lookupKey] = newWorker;

        state.OnLastSubscriberRemoved += (_, _) =>
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

    private void WireQueryStateWithEvictionPolicy<TKey, TResponse>(QueryState<TKey, TResponse> state)
        where TKey : ITuple
    {
        state.OnFirstSubscriberAdded += key => _evictionPolicy.CancelEviction(key);
        state.OnLastSubscriberRemoved +=
            (key, cacheOptions) => _evictionPolicy.RegisterForEviction(key, cacheOptions.GcTime);
    }
}