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
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
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
        ThrowIfDisposedLocked();

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
        if (_persistence is not null)
        {
            // Flagged here rather than inside the task, so the query never reports itself
            // empty in the window between being created and the load starting.
            newState.BeginRestore();
            _ = Task.Run(() => LoadPersistedAsync(newState));
        }

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
    /// Cancels the in-flight fetch of every query under the key prefix, including the query at
    /// the prefix itself, and completes once they have all unwound.
    /// </summary>
    /// <remarks>
    /// <para>Awaiting this is what makes it useful before an optimistic update. A refetch that
    /// started before the mutation would otherwise land after it and overwrite the optimistic
    /// value the caller just wrote. Once this completes, no result from a cancelled fetch can
    /// still reach the query, so writing to <see cref="IQueryState{TRes}.Data"/> afterwards
    /// stands until the next fetch.</para>
    /// <para>Matches by prefix, the same set as
    /// <see cref="Invalidate(ITuple)"/>, so the pair can be used on the same key.</para>
    /// <para>A cancelled query keeps whatever data it already had and records no error. Use
    /// <see cref="IQueryState.Cancel"/> to stop one query without waiting.</para>
    /// </remarks>
    /// <param name="keySegments">The key prefix to cancel under.</param>
    public Task CancelAsync(ITuple keySegments)
    {
        List<IQueryWorker> workers;

        lock (_gate)
        {
            var node = _registry.PeekNode(keySegments);
            workers = node is null ? [] : QueryRegistry.WorkersFrom(node);
        }

        if (workers.Count == 0) return Task.CompletedTask;

        // Every cancellation is requested before any unwind is awaited, so a slow handler on
        // one query does not leave the next one fetching while this call waits.
        return Task.WhenAll(workers.Select(worker => worker.CancelCurrentFetchAsync()));
    }

    /// <summary>
    /// Cancels the in-flight fetch of every query under the string key prefix and completes
    /// once they have all unwound.
    /// </summary>
    /// <param name="key">The string key prefix to cancel under.</param>
    public Task CancelAsync(string key) => CancelAsync(ValueTuple.Create(key));

    /// <summary>
    /// Prefetches data into the registry without subscribing.
    /// Fire-and-forget - triggers fetch immediately, no return value.
    /// Useful for preloading data before component mounts.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="queryOptions">Query configuration.</param>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public void PrefetchQuery<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        // Registered before returning, so a key already taken by another result type is
        // reported to the caller rather than lost in an unobserved task.
        PrepareRun(options);
        _ = PrefetchCoreAsync(options);
    }

    /// <summary>
    /// Runs a prefetch that nobody awaits, absorbing the failures a caller would otherwise
    /// have handled.
    /// </summary>
    /// <remarks>
    /// A prefetch is fire and forget by contract, and a handler's own exception is already
    /// recorded on the query. What is left here is the run failing to happen at all, which a
    /// discarded task would surface as an unobserved task exception long after the fact.
    /// </remarks>
    private async Task PrefetchCoreAsync<TKey, TRes>(QueryOptions<TKey, TRes> options)
        where TKey : ITuple
    {
        try
        {
            await RunAndReleaseAsync(options);
        }
        catch
        {
            // Nobody is waiting for this, and the query carries whatever state the attempt left
        }
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
    /// <param name="cancellationToken">
    /// Abandons the wait, not the fetch. Cancelling it ends this await with an
    /// <see cref="OperationCanceledException"/> while the fetch runs on and its result still
    /// lands in the registry. That is deliberate: an autocomplete that abandons a request on
    /// the next keystroke still wants the answer cached for the backspace that follows.
    /// Use <see cref="CancelAsync(ITuple)"/> to stop the fetch itself.
    /// </param>
    /// <returns>The fetched data.</returns>
    /// <exception cref="Exception">Throws if the query fails.</exception>
    /// <exception cref="OperationCanceledException">
    /// The fetch was cancelled, or <paramref name="cancellationToken"/> was.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<TRes> FetchQueryAsync<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        CancellationToken cancellationToken = default)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        // Registered before the await, so a key already taken by another result type throws
        // from the call rather than from the task it returned.
        PrepareRun(options);

        // WaitAsync rather than a token passed into the run: the fetch is shared with every
        // other caller joined to it, and one caller walking away must not take it from them.
        var (state, outcome) = await RunAndReleaseAsync(options).WaitAsync(cancellationToken);

        // Checked before the exception, which belongs to the query rather than to this call.
        // A fetch cancelled while an older failure still sat on the state would otherwise
        // throw that failure, reporting an error this call never hit.
        if (outcome == FetchOutcome.Cancelled)
        {
            // Returning Data here would hand back null, or another caller's older result, as
            // though this call had fetched it. The caller asked for data and there is none.
            throw new OperationCanceledException(
                $"Query {DescribeKey(state.Key)} was cancelled before it produced data.");
        }

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
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
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
    /// Captures every query that currently holds data.
    /// </summary>
    /// <remarks>
    /// The dehydrate half of moving a prerender's work to the client. Pending queries and
    /// failed ones produce no snapshot, so a failure is never carried across as though it were
    /// a result and the client simply fetches for itself.
    /// </remarks>
    /// <returns>A snapshot per resolved query, in no particular order.</returns>
    public IReadOnlyList<QuerySnapshot> SnapshotResolvedQueries()
    {
        List<IQueryState> states;

        lock (_gate) states = QueryRegistry.StatesFrom(_registry.Root);

        var snapshots = new List<QuerySnapshot>(states.Count);

        foreach (var state in states)
        {
            if (state.Snapshot() is { } snapshot) snapshots.Add(snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// Re-applies options to an existing subscription whose key has not changed.
    /// </summary>
    /// <remarks>
    /// Called on every re-render that reaches the same query, so that Enabled, StaleTime,
    /// RefetchInterval, RetryOptions and CacheOptions stay reactive rather than being frozen at
    /// the render that first created the query. Does nothing when the query has since left the
    /// registry.
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="observer">The observer returned by the original Subscribe call.</param>
    /// <param name="queryOptions">The rebuilt query configuration.</param>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public void ApplyOptions<TKey, TRes>(QueryObserver<TRes> observer, QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        if (observer.Query is not QueryState<TKey, TRes> state) return;

        QueryWorker<TKey, TRes> worker;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            // An observer outlives its state only if the state was evicted, which cannot happen
            // while it has one. Checked anyway: driving a state the registry has let go would
            // give it a worker nothing owns.
            if (!ReferenceEquals(_registry.PeekNode(state.Key)?.State, state)) return;

            worker = GetOrCreateWorker(state);
        }

        worker.ApplyOptions(options, observer);
    }

    /// <summary>
    /// Stops every worker and detaches from the eviction policy.
    /// Called by the DI container when the owning scope ends.
    /// </summary>
    /// <remarks>
    /// Idempotent. Afterwards the entry points a live render reaches throw
    /// <see cref="ObjectDisposedException"/>, while unsubscribing, cancelling and disposing an
    /// observer stay silent no-ops.
    /// </remarks>
    public void Dispose()
    {
        List<IQueryWorker> workers;

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
    /// Refuses work that would put anything back into a registry this client has already
    /// emptied. Callers hold the lock, so the answer cannot change under them.
    /// </summary>
    /// <remarks>
    /// Guards the entry points a live render reaches: Subscribe, PrefetchQuery,
    /// FetchQueryAsync, GetOrCreateQuery and ApplyOptions. A component rendering against a
    /// disposed client is a bug, and one that silently created a query would also create a
    /// worker with nothing left to dispose it.
    ///
    /// Teardown is deliberately not guarded. Unsubscribe, CancelAsync and observer disposal
    /// stay silent no-ops, because Blazor does not specify whether component disposal runs
    /// before or after the DI scope that owns this client.
    /// </remarks>
    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    /// <summary>
    /// Registers a query and its worker for a run that no observer asked for.
    /// </summary>
    /// <param name="options">Options that have already been through the plugin pipeline.</param>
    private (QueryState<TKey, TRes> State, QueryWorker<TKey, TRes> Worker) PrepareRun<TKey, TRes>(
        QueryOptions<TKey, TRes> options) where TKey : ITuple
    {
        lock (_gate)
        {
            var state = GetOrCreateQueryLocked(options);
            return (state, GetOrCreateWorker(state));
        }
    }

    /// <summary>
    /// Runs a query for a caller with no observer of its own, releasing it again afterwards.
    /// </summary>
    /// <returns>The state that was run, and what the run did.</returns>
    private async Task<(QueryState<TKey, TRes> State, FetchOutcome Outcome)> RunAndReleaseAsync<TKey, TRes>(
        QueryOptions<TKey, TRes> options) where TKey : ITuple
    {
        // A worker released by whoever else was using this query refuses to run. Take a fresh
        // one rather than returning data the caller never actually fetched.
        for (var attempt = 0; attempt < MaxRunAttempts; attempt++)
        {
            // State and worker are taken together, every attempt, because an eviction can land
            // between two of them. It prunes the node, and a worker created afterwards for the
            // evicted state would sit on a node the registry has since refilled with a second
            // state for the same key. The next subscriber would then get that second state and
            // a worker driving the first, and would wait for a fetch that never reaches it.
            var (state, worker) = PrepareRun(options);

            var outcome = FetchOutcome.NotRun;

            try
            {
                outcome = await worker.RunAsync();
            }
            finally
            {
                ReleaseIfUnobserved(state);
            }

            if (outcome != FetchOutcome.NotRun) return (state, outcome);
        }

        throw new InvalidOperationException(
            $"Query {DescribeKey(options.Key)} could not be run: its worker was released " +
            $"{MaxRunAttempts} times while the fetch was starting.");
    }

    /// <summary>
    /// Returns the worker for a state, creating it when missing. Callers hold the lock.
    /// </summary>
    private QueryWorker<TKey, TRes> GetOrCreateWorker<TKey, TRes>(QueryState<TKey, TRes> state)
        where TKey : ITuple
    {
        // Also covers the retry loop in RunAndReleaseAsync, which is the one caller that can
        // arrive here after the client was disposed under an in-flight fetch.
        ThrowIfDisposedLocked();

        var node = _registry.GetOrCreateNode(state.Key);

        if (node.Worker is QueryWorker<TKey, TRes> existing) return existing;

        var worker = new QueryWorker<TKey, TRes>(
            _defaultOptions, _serviceProvider, state, _persistence);
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
        IQueryWorker? worker;

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
        IQueryWorker? worker;

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
        try
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

            // TryRestore refuses once a fetch has landed, so the older stored data cannot
            // overwrite a fresher result that arrived while this load was in flight.
            state.TryRestore(persisted.Data, persisted.LastUpdatedAt);
        }
        finally
        {
            // One notification for the whole restore, after it ends. Ending it runs whatever
            // decision was deferred to it, so a query that found nothing stored is already
            // fetching by the time observers hear anything, and a query that found fresh data
            // is already resolved. Neither reports a moment with nothing in progress.
            state.CompleteRestore();
            NotifyChangedSafely(state);
        }
    }

    /// <summary>
    /// Notifies observers without letting one of them break the restore. This runs on a
    /// detached task, so an exception escaping here would go unobserved.
    /// </summary>
    private static void NotifyChangedSafely<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        try
        {
            state.NotifyChanged();
        }
        catch
        {
            // An observer that cannot render is the observer's problem, not the query's
        }
    }

    private static string DescribeKey(ITuple key)
    {
        var segments = new string[key.Length];
        for (var i = 0; i < key.Length; i++) segments[i] = key[i]?.ToString() ?? "null";
        return $"({string.Join(", ", segments)})";
    }
}
