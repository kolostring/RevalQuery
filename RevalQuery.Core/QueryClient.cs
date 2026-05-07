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
/// Coordinates state management, caching, subscription, and worker orchestration.
/// </summary>
public sealed class QueryClient
{
    private readonly Dictionary<int, IQueryState> _stateLookup = new();
    private readonly Dictionary<int, IDisposable> _workerLookup = new();
    private readonly ICacheStorage _cacheStorage;
    private readonly ICacheEvictionPolicy _evictionPolicy;
    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _defaultOptions;

    /// <summary>
    /// Creates a new QueryClient instance.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving dependencies in handlers.</param>
    /// <param name="defaultOptions">Default options for all queries (plugins, cache, retry, fetch).</param>
    /// <param name="cacheStorage">Optional custom cache storage implementation.</param>
    /// <param name="evictionPolicy">Optional custom eviction policy implementation.</param>
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

    /// <summary>
    /// Gets or creates a query state for the given options.
    /// Won't start fetching - prefer Subscribe() for component usage.
    /// </summary>
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

    /// <summary>
    /// Invalidates a query by key, triggering refetch on next access.
    /// Invalidates all queries under the key prefix recursively.
    /// </summary>
    /// <param name="keySegments">The key to invalidate.</param>
    public void Invalidate(ITuple keySegments)
    {
        var node = _cacheStorage.PeekNode(keySegments);
        if (node != null) NotifyInvalidationRecursive(node);
    }

    /// <summary>
    /// Invalidates a query by string key.
    /// </summary>
    /// <param name="key">The string key to invalidate.</param>
    public void Invalidate(string key) => Invalidate(ValueTuple.Create(key));

    /// <summary>
    /// Cancels any in-progress fetch for the given key.
    /// </summary>
    /// <param name="keySegments">The key to cancel.</param>
    public void Cancel(ITuple keySegments)
    {
        FindQuery(keySegments)?.Cancel();
    }

    /// <summary>
    /// Cancels any in-progress fetch for the given string key.
    /// </summary>
    /// <param name="key">The string key to cancel.</param>
    public void Cancel(string key) => Cancel(ValueTuple.Create(key));

    /// <summary>
    /// Prefetches data into cache without subscribing.
    /// Fire-and-forget - triggers fetch immediately, no return value.
    /// Useful for preloading data before component mounts.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="queryOptions">Query configuration.</param>
    public void PrefetchQuery<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var state = GetOrCreateQuery(queryOptions);

        var lookupKey = CacheKeyCalculator.GetHashCode(state.Key);
        if (!_workerLookup.TryGetValue(lookupKey, out var worker))
        {
            var newWorker = new QueryWorker<TKey, TRes>(_defaultOptions, _serviceProvider, state, null);
            _workerLookup[lookupKey] = newWorker;
            worker = newWorker;
        }

        _ = ((QueryWorker<TKey, TRes>)worker!).RunAsync();
    }

    /// <summary>
    /// Fetches data and returns the result.
    /// Unlike PrefetchQuery, this awaits completion and returns data.
    /// Throws exception on failure.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="queryOptions">Query configuration.</param>
    /// <returns>The fetched data.</returns>
    /// <exception cref="Exception">Throws if the query fails.</exception>
    public async Task<TRes> FetchQueryAsync<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var state = GetOrCreateQuery(queryOptions);

        var lookupKey = CacheKeyCalculator.GetHashCode(state.Key);
        if (!_workerLookup.TryGetValue(lookupKey, out var worker))
        {
            var newWorker = new QueryWorker<TKey, TRes>(_defaultOptions, _serviceProvider, state, null);
            _workerLookup[lookupKey] = newWorker;
            worker = newWorker;
        }

        await ((QueryWorker<TKey, TRes>)worker!).RunAsync();
        if (state.IsException)
        {
            throw state.Exception!;
        }

        return state.Data!;
    }

    /// <summary>
    /// Finds an existing query state by key.
    /// Returns null if not found.
    /// </summary>
    /// <param name="keySegments">The key to find.</param>
    public IQueryState? FindQuery(ITuple keySegments)
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        return _stateLookup.GetValueOrDefault(lookupKey);
    }

    /// <summary>
    /// Finds an existing query state by key.
    /// Returns null if not found.
    /// Automatically casts the result to the correct type.
    /// </summary>
    /// <param name="keySegments">The key to find.</param>
    public QueryState<TKey, TRes>? FindQuery<TRes, TKey>(TKey keySegments) where TKey : ITuple
    {
        var lookupKey = CacheKeyCalculator.GetHashCode(keySegments);
        return (QueryState<TKey, TRes>?)_stateLookup.GetValueOrDefault(lookupKey);
    }

    /// <summary>
    /// Finds an existing query state by string key.
    /// Returns null if not found.
    /// </summary>
    /// <param name="key">The string key.</param>
    public IQueryState? FindQuery(string key) => FindQuery(ValueTuple.Create(key));

    /// <summary>
    /// Finds an existing query state by string key.
    /// Returns null if not found.
    /// Automatically casts the result to the correct type.
    /// </summary>
    /// <param name="key">The string key.</param>
    public QueryState<ValueTuple<string>, TRes>? FindQuery<TRes>(string key) => (QueryState<ValueTuple<string>, TRes>?)FindQuery(ValueTuple.Create(key));

    /// <summary>
    /// Finds all queries under a key prefix.
    /// Used for bulk invalidation - returns all child query states.
    /// </summary>
    /// <param name="keySegments">The prefix key.</param>
    /// <returns>Collection of matching query states.</returns>
    public ICollection<IQueryState> FindQueries(ITuple keySegments)
    {
        var node = _cacheStorage.PeekNode(keySegments);
        if (node == null) return [];

        var childNodes = _cacheStorage.GetChildNodes(node);

        return [.. childNodes.Select(child => _stateLookup.GetValueOrDefault(child.KeyHashCode)).OfType<IQueryState>()];
    }

    /// <summary>
    /// Finds all queries under a string key prefix.
    /// </summary>
    public ICollection<IQueryState> FindQueries(string key) => FindQueries(ValueTuple.Create(key));

    /// <summary>
    /// Subscribes a component to a query.
    /// Returns an observer that manages the subscription lifecycle.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="queryOptions">Query configuration including key and handler.</param>
    /// <param name="onStateHasChanged">Callback to invoke StateHasChanged on the component.</param>
    /// <returns>A QueryObserver that should be disposed when component is disposed.</returns>
    public QueryObserver<TRes> Subscribe<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions, Action onStateHasChanged)
        where TKey : ITuple
    {
        _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var state = GetOrCreateQuery(
            queryOptions
        );

        var observer = new QueryObserver<TRes>(
            state,
            onStateHasChanged,
            queryOptions.Enabled
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