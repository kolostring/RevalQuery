using System.Runtime.CompilerServices;

namespace RevalQuery.Core;

public class QueryClient
{
    private readonly Dictionary<int, IQueryState> _stateLookup = new();
    private readonly Dictionary<int, IDisposable> _workerLookup = new();
    private readonly CacheNode _cacheTrie = new(0);
    private readonly QueryGarbageCollector _gc = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _options;

    public QueryClient(IServiceProvider serviceProvider, RevalQueryOptions options)
    {
        _serviceProvider = serviceProvider;
        _gc.OnEvictionRequired += HandleEviction;
        _options = options;
    }

    public QueryState<TKey, TRes> GetOrCreateQuery<TKey, TRes>(
        TKey keySegments,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler,
        FetchOptions? fetchOptions,
        CacheOptions? cacheOptions
    ) where TKey : ITuple
    {
        int lookupKey = GetHash(keySegments).ToHashCode();
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

        GetOrCreateCacheInstance(keySegments);

        var sanitizedFetchOptions = (fetchOptions ?? new FetchOptions()).PatchNullFields(_options.FetchOptions);

        var newState = new QueryState<TKey, TRes>(
            keySegments,
            handler,
            cacheOptions ?? _options.CacheOptions,
            sanitizedFetchOptions
        );
        _stateLookup[lookupKey] = newState;

        WireQueryStateWithGarbageCollector(newState);

        return newState;
    }

    public void Invalidate(ITuple keySegments)
    {
        var node = PeekCacheInstance(keySegments);
        if (node != null)
        {
            NotifyInvalidationRecursive(node);
        }
    }

    public void Invalidate(string key)
    {
        var node = PeekCacheInstance(key);
        if (node != null)
        {
            NotifyInvalidationRecursive(node);
        }
    }

    public IQueryState? FindQuery(ITuple keySegments)
    {
        int lookupKey = GetHash(keySegments).ToHashCode();
        return _stateLookup.GetValueOrDefault(lookupKey);
    }

    public ICollection<IQueryState> FindQueries(ITuple keySegments)
    {
        var node = PeekCacheInstance(keySegments);

        return node != null ? RecursiveQueriesRetrieval(node) : [];
    }

    public QueryObserver Subscribe<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions, Action onStateHasChanged)
        where TKey : ITuple
    {
        _options.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var prerenderState = new QueryState<TKey, TRes>(
            queryOptions.Key,
            queryOptions.Handler,
            _options.CacheOptions,
            _options.FetchOptions
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
        int lookupKey = GetHash(state.Key).ToHashCode();

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
            if (_workerLookup.Remove(lookupKey, out var worker))
            {
                worker.Dispose();
            }
        };
        newWorker.RunIfStale();
    }

    private void HandleEviction(ITuple key)
    {
        var hash = GetHash(key).ToHashCode();
        _stateLookup.Remove(hash);
        bool nodeFoundAndDeleted = PruneRecursive(_cacheTrie, key, 0);

        if (!nodeFoundAndDeleted)
        {
            throw new InvalidOperationException($"Couldn't delete node with key {key}");
        }
    }

    private bool PruneRecursive(CacheNode current, ITuple key, int keyIndex)
    {
        if (keyIndex >= key.Length)
        {
            return current.Children.Count == 0 && !_stateLookup.ContainsKey(current.KeyHashCode);
        }

        var segment = key[keyIndex]?.ToString() ?? "null";

        if (!current.Children.TryGetValue(segment, out var child)) return false;

        bool shouldDeleteChild = PruneRecursive(child, key, keyIndex + 1);

        if (!shouldDeleteChild) return false;

        current.Children.Remove(segment);

        return current.Children.Count == 0 && !_stateLookup.ContainsKey(current.KeyHashCode);
    }

    private void GetOrCreateCacheInstance(ITuple keySegments)
    {
        var current = _cacheTrie;
        var hashCode = new HashCode();

        for (int i = 0; i < keySegments.Length; i++)
        {
            string segment = keySegments[i]?.ToString() ?? "null";
            hashCode.Add(keySegments[i]);

            if (!current.Children.TryGetValue(segment, out CacheNode? value))
            {
                value = new CacheNode(hashCode.ToHashCode());
                current.Children[segment] = value;
            }

            current = value;
        }
    }

    private ICollection<IQueryState> RecursiveQueriesRetrieval(
        CacheNode node)
    {
        List<IQueryState> retrieved = [];

        var state = _stateLookup.GetValueOrDefault(node.KeyHashCode);
        if (state != null)
        {
            retrieved.Add(state);
        }

        foreach (var child in node.Children)
        {
            retrieved.AddRange(RecursiveQueriesRetrieval(child.Value));
        }

        return retrieved;
    }

    private CacheNode? PeekCacheInstance(ITuple keySegments)
    {
        var current = _cacheTrie;
        for (int i = 0; i < keySegments.Length; i++)
        {
            string segment = keySegments[i]?.ToString() ?? "null";

            if (!current.Children.TryGetValue(segment, out CacheNode? value))
            {
                return null;
            }

            current = value;
        }

        return current;
    }

    private CacheNode? PeekCacheInstance(string key)
    {
        return _cacheTrie.Children.GetValueOrDefault(key);
    }

    private void NotifyInvalidationRecursive(CacheNode node)
    {
        var state = _stateLookup.GetValueOrDefault(node.KeyHashCode);
        state?.NotifyInvalidated();

        foreach (var child in node.Children.Values)
        {
            NotifyInvalidationRecursive(child);
        }
    }

    private static HashCode GetHash(ITuple key)
    {
        var hash = new HashCode();

        for (int i = 0; i < key.Length; i++)
        {
            hash.Add(key[i]);
        }

        return hash;
    }

    private sealed class CacheNode(int keyHashCode)
    {
        public int KeyHashCode { get; init; } = keyHashCode;
        public Dictionary<string, CacheNode> Children { get; set; } = new();
    }

    private void WireQueryStateWithGarbageCollector<TKey, TResponse>(QueryState<TKey, TResponse> state)
        where TKey : ITuple
    {
        state.OnFirstSubscriberAdded += key => _gc.CancelEviction(key);
        state.OnLastSubscriberRemoved += (key, cacheOptions) => _gc.RegisterForEviction(key, cacheOptions);
    }
}