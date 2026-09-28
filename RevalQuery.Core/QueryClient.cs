using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core;

/// <summary>
/// Main entry point for query management.
/// Owns the registry of live query states, the workers driving them, and the eviction policy.
/// </summary>
/// <remarks>
/// Safe to call from any thread. One client serves one user session: the registry, the
/// eviction policy and the client itself share a lifetime, and sharing a client between
/// users leaks their data to each other.
/// </remarks>
public sealed class QueryClient : IDisposable
{
    private const int MaxRunAttempts = 3;

    private readonly object _gate = new();
    private readonly QueryRegistry _registry = new();
    private readonly ICacheEvictionPolicy _evictionPolicy;
    private readonly bool _ownsEvictionPolicy;
    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _defaultOptions;
    private readonly IQueryPersistence? _persistence;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new QueryClient instance.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving dependencies in handlers.</param>
    /// <param name="defaultOptions">Default options for all queries (plugins, cache, retry, fetch).</param>
    /// <param name="evictionPolicy">Optional custom eviction policy. One is created when omitted.</param>
    /// <param name="persistence">Optional durable store queries are loaded from and saved to.</param>
    public QueryClient(
        IServiceProvider serviceProvider,
        RevalQueryOptions defaultOptions,
        ICacheEvictionPolicy? evictionPolicy = null,
        IQueryPersistence? persistence = null
    )
    {
        _serviceProvider = serviceProvider;
        _defaultOptions = defaultOptions;
        _persistence = persistence;
        _ownsEvictionPolicy = evictionPolicy is null;
        _evictionPolicy = evictionPolicy ?? new TtlQueryGarbageCollector(defaultOptions);
        _evictionPolicy.OnEvictionRequired += HandleEviction;
    }

    /// <summary>
    /// Gets or creates a query state for the given options.
    /// Won't start fetching - prefer Subscribe() for component usage.
    /// </summary>
    public QueryState<TKey, TRes> GetOrCreateQuery<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions
    ) where TKey : ITuple
    {
        lock (_gate) return GetOrCreateQueryLocked(queryOptions);
    }

    /// <summary>
    /// Returns the state for these options, creating and wiring it when the key is free.
    /// Callers hold the lock.
    /// </summary>
    private QueryState<TKey, TRes> GetOrCreateQueryLocked<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        var node = _registry.GetOrCreateNode(queryOptions.Key);

        if (node.State is not null)
        {
            if (node.State is QueryState<TKey, TRes> existing) return existing;

            throw new InvalidOperationException(
                $"Query {DescribeKey(queryOptions.Key)} is already registered with result type " +
                $"{node.State.GetType().GenericTypeArguments[1].Name}, but {typeof(TRes).Name} was requested.");
        }

        var newState = new QueryState<TKey, TRes>(
            queryOptions.Key,
            queryOptions.Handler,
            queryOptions.FetchOptions,
            queryOptions.RetryOptions,
            queryOptions.CacheOptions
        );

        node.State = newState;
        WireStateLifecycle(newState);

        // Off the lock from the start: an adapter is free to block, and holding the registry
        // while it does would stall every other query.
        if (_persistence is not null) node.Restore = Task.Run(() => LoadPersistedAsync(newState));

        return newState;
    }

    /// <summary>
    /// Invalidates a query by key, triggering refetch on next access.
    /// Invalidates all queries under the key prefix recursively.
    /// </summary>
    /// <param name="keySegments">The key to invalidate.</param>
    public void Invalidate(ITuple keySegments)
    {
        foreach (var state in StatesUnder(keySegments)) state.NotifyInvalidated();
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
    /// Prefetches data into the registry without subscribing.
    /// Fire-and-forget - triggers fetch immediately, no return value.
    /// Useful for preloading data before component mounts.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="queryOptions">Query configuration.</param>
    public void PrefetchQuery<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        // Registered before returning, so a key already taken by another result type is
        // reported to the caller rather than lost in an unobserved task.
        var (state, worker) = PrepareRun(queryOptions);
        _ = RunAndReleaseAsync(state, worker);
    }

    /// <summary>
    /// Fetches data and returns the result.
    /// Unlike PrefetchQuery, this awaits completion and returns data.
    /// Callers arriving while a fetch is already running join it instead of starting a second one.
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
        var (state, worker) = PrepareRun(queryOptions);

        await RunAndReleaseAsync(state, worker);

        if (state.IsException) throw state.Exception!;

        return state.Data!;
    }

    /// <summary>
    /// Finds an existing query state by key.
    /// Returns null if not found.
    /// </summary>
    /// <param name="keySegments">The key to find.</param>
    public IQueryState? FindQuery(ITuple keySegments)
    {
        lock (_gate) return _registry.PeekNode(keySegments)?.State;
    }

    /// <summary>
    /// Finds an existing query state by key.
    /// Returns null if not found, or if the query was registered with a different result type.
    /// </summary>
    /// <param name="keySegments">The key to find.</param>
    public QueryState<TKey, TRes>? FindQuery<TRes, TKey>(TKey keySegments) where TKey : ITuple
    {
        return FindQuery(keySegments) as QueryState<TKey, TRes>;
    }

    /// <summary>
    /// Finds an existing query state by string key.
    /// Returns null if not found.
    /// </summary>
    /// <param name="key">The string key.</param>
    public IQueryState? FindQuery(string key) => FindQuery(ValueTuple.Create(key));

    /// <summary>
    /// Finds an existing query state by string key.
    /// Returns null if not found, or if the query was registered with a different result type.
    /// </summary>
    /// <param name="key">The string key.</param>
    public QueryState<ValueTuple<string>, TRes>? FindQuery<TRes>(string key) =>
        FindQuery<TRes, ValueTuple<string>>(ValueTuple.Create(key));

    /// <summary>
    /// Finds all queries under a key prefix, including the query at the prefix itself.
    /// Used for bulk invalidation.
    /// </summary>
    /// <param name="keySegments">The prefix key.</param>
    /// <returns>Collection of matching query states.</returns>
    public ICollection<IQueryState> FindQueries(ITuple keySegments) => StatesUnder(keySegments);

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
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        QueryObserver<TRes> observer;
        QueryWorker<TKey, TRes> worker;

        // Registering the query, subscribing to it and giving it a worker happen together.
        // Split apart, an eviction landing in the gap would drop the state a component is
        // about to render, and the next subscriber to the same key would get a second one.
        lock (_gate)
        {
            var state = GetOrCreateQueryLocked(options);

            observer = new QueryObserver<TRes>(
                state,
                onStateHasChanged,
                options.Enabled
            );

            worker = GetOrCreateWorker(state);
        }

        worker.RunIfStale();

        return observer;
    }

    /// <summary>
    /// Stops every worker and detaches from the eviction policy.
    /// Called by the DI container when the owning scope ends.
    /// </summary>
    public void Dispose()
    {
        List<IDisposable> workers;

        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            workers = QueryRegistry.WorkersFrom(_registry.Root);
            _registry.Clear();
        }

        _evictionPolicy.OnEvictionRequired -= HandleEviction;

        foreach (var worker in workers) worker.Dispose();

        // Only stop a policy this client created. An injected one is the container's to dispose.
        if (_ownsEvictionPolicy) _ = _evictionPolicy.StopAsync();
    }

    /// <summary>
    /// Registers a query and its worker for a run that no observer asked for.
    /// </summary>
    private (QueryState<TKey, TRes> State, QueryWorker<TKey, TRes> Worker) PrepareRun<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions) where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        lock (_gate)
        {
            var state = GetOrCreateQueryLocked(options);
            return (state, GetOrCreateWorker(state));
        }
    }

    private async Task RunAndReleaseAsync<TKey, TRes>(QueryState<TKey, TRes> state, QueryWorker<TKey, TRes> worker)
        where TKey : ITuple
    {
        // A worker released by whoever else was using this query refuses to run. Take a fresh
        // one rather than returning data the caller never actually fetched.
        for (var attempt = 0; attempt < MaxRunAttempts; attempt++)
        {
            var ran = false;

            try
            {
                ran = await worker.RunAsync();
            }
            finally
            {
                ReleaseIfUnobserved(state);
            }

            if (ran) return;

            lock (_gate) worker = GetOrCreateWorker(state);
        }

        throw new InvalidOperationException(
            $"Query {DescribeKey(state.Key)} could not be run: its worker was released " +
            $"{MaxRunAttempts} times while the fetch was starting.");
    }

    /// <summary>
    /// Returns the worker for a state, creating it when missing. Callers hold the lock.
    /// </summary>
    private QueryWorker<TKey, TRes> GetOrCreateWorker<TKey, TRes>(QueryState<TKey, TRes> state)
        where TKey : ITuple
    {
        var node = _registry.GetOrCreateNode(state.Key);

        if (node.Worker is QueryWorker<TKey, TRes> existing) return existing;

        var worker = new QueryWorker<TKey, TRes>(
            _defaultOptions, _serviceProvider, state, _persistence, node.Restore);
        node.Worker = worker;

        return worker;
    }

    /// <summary>
    /// Wires a state's subscriber lifecycle once, when it enters the registry. A query with
    /// no observers stops its worker and goes on the eviction policy's list; a query that
    /// gains one comes back off it.
    /// </summary>
    private void WireStateLifecycle<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        state.OnFirstSubscriberAdded += key => _evictionPolicy.CancelEviction(key);
        state.OnLastSubscriberRemoved += unobserved =>
        {
            DisposeWorker(unobserved.Key);
            _evictionPolicy.RegisterForEviction(unobserved.Key, unobserved.CacheOptions);
        };
    }

    /// <summary>
    /// Stops the worker for a query nobody is watching and puts the query on the eviction list.
    /// Used after a prefetch or a one-off fetch, which create no observer of their own.
    /// </summary>
    private void ReleaseIfUnobserved<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        if (state.HasObservers) return;

        DisposeWorker(state.Key);
        _evictionPolicy.RegisterForEviction(state.Key, state.CacheOptions);
    }

    private void DisposeWorker(ITuple key)
    {
        IDisposable? worker;

        lock (_gate)
        {
            var node = _registry.PeekNode(key);
            if (node is null || node.State?.HasObservers == true) return;

            worker = node.Worker;
            node.Worker = null;
        }

        worker?.Dispose();
    }

    private void HandleEviction(ITuple key)
    {
        IDisposable? worker;

        lock (_gate)
        {
            var node = _registry.PeekNode(key);
            if (node?.State is null || node.State.HasObservers) return;

            worker = node.Worker;
            node.Worker = null;
            node.State = null;
            _registry.PruneNode(key);
        }

        worker?.Dispose();
    }

    private List<IQueryState> StatesUnder(ITuple keySegments)
    {
        lock (_gate)
        {
            var node = _registry.PeekNode(keySegments);
            return node is null ? [] : QueryRegistry.StatesFrom(node);
        }
    }

    /// <summary>
    /// Loads a query's data from persistence, unless a fetch has already produced some.
    /// </summary>
    private async Task LoadPersistedAsync<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        PersistedQuery<TRes>? persisted;

        try
        {
            persisted = await _persistence!.LoadAsync<TRes>(state.Key);
        }
        catch
        {
            // A persistence adapter failing leaves the query to fetch normally.
            // Reporting the failure is the adapter's own job.
            return;
        }

        if (persisted is null) return;

        // TryRestore refuses once a fetch has landed, so the older stored data cannot overwrite
        // a fresher result that arrived while this load was in flight.
        if (state.TryRestore(persisted.Data, persisted.LastUpdatedAt)) state.NotifyChanged();
    }

    private static string DescribeKey(ITuple key)
    {
        var segments = new string[key.Length];
        for (var i = 0; i < key.Length; i++) segments[i] = key[i]?.ToString() ?? "null";
        return $"({string.Join(", ", segments)})";
    }
}
