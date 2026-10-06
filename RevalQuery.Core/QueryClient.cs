using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;
using RevalQuery.Core.Tracking;

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
    /// Creates a tracker: the place one component reads its queries and mutations through, so it
    /// holds no observers of its own.
    /// </summary>
    /// <remarks>
    /// The tracker lives until the handle from its <c>Attach</c> is disposed. A framework adapter
    /// attaches to it; see <see cref="QueryTracker"/>. When <c>AddRevalQuery</c> registered the
    /// client, a tracker can be injected instead.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public QueryTracker CreateTracker()
    {
        lock (_gate) ThrowIfDisposedLocked();

        return new QueryTracker(this);
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

        if (_persistence is not null)
        {
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

        return Task.WhenAll(workers.Select(worker => worker.CancelCurrentFetchAsync()));
    }

    /// <summary>
    /// Cancels the in-flight fetch of every query under the string key prefix and completes
    /// once they have all unwound.
    /// </summary>
    /// <param name="key">The string key prefix to cancel under.</param>
    public Task CancelAsync(string key) => CancelAsync(ValueTuple.Create(key));

    /// <summary>
    /// Fetches a query's data and returns it, or returns what the registry already holds when
    /// that data is still fresh.
    /// </summary>
    /// <remarks>
    /// <para>The whole imperative surface. There is no separate prefetch: a caller that does
    /// not want the result discards the task and handles its failure, which is the same call
    /// with a different error policy rather than a different operation. A caller that wants
    /// the cache honoured however old the data is asks for it on the query, with
    /// <see cref="FetchOptionsBuilder.NeverStale"/>.</para>
    /// <para>Staleness is judged from the options passed to this call, against the data the
    /// query already holds. It is the same question <see cref="Subscribe"/> asks, answered by
    /// the same method, so a component and a route loader looking at one key agree about
    /// whether it needs refetching.</para>
    /// <para>These options decide this call. They are written onto the query only when this
    /// call is what creates it; against a query that already exists they are not, so a loader
    /// asking for <see cref="FetchOptionsBuilder.NeverStale"/> serves itself from cache without
    /// changing what any subscriber asked for. Nor can it freeze a key it did create, because a
    /// component's options are adopted when it subscribes and again on every re-render.</para>
    /// <para>Callers arriving while a fetch is already running join it rather than starting a
    /// second one.</para>
    /// <para>The query is released again afterwards. Nothing subscribed to it here, so it goes
    /// on the eviction list and must outlive its own freshness window to be worth caching:
    /// <c>GcTime</c> has to exceed <c>StaleTime</c>. The defaults satisfy that on their own --
    /// <c>StaleTime</c> is zero and <c>GcTime</c> five minutes -- but a long <c>StaleTime</c>
    /// needs a <c>GcTime</c> raised to match, or the entry is evicted before it is ever served
    /// from cache.</para>
    /// </remarks>
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
    /// <returns>The fetched data, or the cached data when it was still fresh.</returns>
    /// <exception cref="Exception">Throws if the query fails.</exception>
    /// <exception cref="OperationCanceledException">
    /// The fetch was cancelled, or <paramref name="cancellationToken"/> was.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<TRes> QueryAsync<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        CancellationToken cancellationToken = default)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var (state, _) = PrepareRun(options);

        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseIfUnobserved(state);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!state.IsStale(_defaultOptions.FetchOptions.Apply(options.FetchOptions)))
        {
            ReleaseIfUnobserved(state);
            return state.Data!;
        }

        var (ran, outcome) = await RunAndReleaseAsync(options).WaitAsync(cancellationToken);

        if (outcome == FetchOutcome.Cancelled)
        {
            throw new OperationCanceledException(
                $"Query {DescribeKey(ran.Key)} was cancelled before it produced data.");
        }

        if (ran.IsException) throw ran.Exception!;

        return ran.Data!;
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
    /// <returns>
    /// A QueryObserver that should be disposed when component is disposed. Hand it new options
    /// with <see cref="QueryObserver{TKey, TRes}.SetOptions"/> on every render and it follows
    /// the key; <see cref="Observe{TKey, TRes}(ref QueryObserver{TKey, TRes}?, QueryOptions{TKey, TRes}, Action)"/> does both steps.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public QueryObserver<TKey, TRes> Subscribe<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions, Action onStateHasChanged)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        QueryObserver<TKey, TRes> observer;
        QueryWorker<TKey, TRes> worker;

        lock (_gate)
        {
            var state = GetOrCreateQueryLocked(options);

            observer = new QueryObserver<TKey, TRes>(
                this,
                state,
                onStateHasChanged,
                options.Enabled
            );

            worker = GetOrCreateWorker(state);
        }

        worker.ApplyOptions(options, observer);

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
    /// Subscribes the first time it is called with a slot, and hands the slot its new options
    /// every time after.
    /// </summary>
    /// <remarks>
    /// <para>The whole of what a component does on a render, for a caller that has nothing like
    /// a <see cref="QueryTracker"/> to do it for them. The slot is the caller's: a field of the
    /// component, dispose it with the component. The observer inside it follows the key, so the
    /// caller never compares one.</para>
    /// <para>Returns the state to read from, which is a different object after a key change.
    /// Call it from the property a render reads rather than caching what it returned.</para>
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="slot">The caller's observer, null until the first call.</param>
    /// <param name="queryOptions">The query configuration for this render.</param>
    /// <param name="onStateHasChanged">Callback to re-render. Used only when the slot is created.</param>
    /// <returns>The query state the slot now watches.</returns>
    /// <exception cref="InvalidOperationException">The key is registered with another result type.</exception>
    /// <exception cref="ObjectDisposedException">The client or the slot's observer was disposed.</exception>
    public IQueryState<TRes> Observe<TKey, TRes>(
        ref QueryObserver<TKey, TRes>? slot,
        QueryOptions<TKey, TRes> queryOptions,
        Action onStateHasChanged)
        where TKey : ITuple
    {
        if (slot is null)
        {
            slot = Subscribe(queryOptions, onStateHasChanged);
        }
        else
        {
            slot.SetOptions(queryOptions);
        }

        return slot.Query;
    }

    /// <summary>
    /// Creates a mutation for a component, handing handlers this client's service provider.
    /// </summary>
    /// <remarks>
    /// A mutation has no key and is not cached, so nothing is registered: the state belongs to
    /// the observer and goes when the caller lets go of it. Hand the observer new options with
    /// <see cref="MutationObserver{TParams, TRes}.SetOptions"/> on every render, or use
    /// <see cref="Observe{TParams, TRes}(ref MutationObserver{TParams, TRes}?, MutationOptions{TParams, TRes}, Action)"/>
    /// to do both.
    /// </remarks>
    /// <typeparam name="TParams">The mutation parameters type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="options">The mutation configuration.</param>
    /// <param name="onStateHasChanged">Callback to invoke StateHasChanged on the component.</param>
    /// <returns>A MutationObserver that should be disposed when the component is.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public MutationObserver<TParams, TRes> CreateMutation<TParams, TRes>(
        MutationOptions<TParams, TRes> options,
        Action onStateHasChanged)
        where TParams : class
    {
        lock (_gate) ThrowIfDisposedLocked();

        return new MutationObserver<TParams, TRes>(
            new MutationState<TParams, TRes>(options, _serviceProvider),
            onStateHasChanged);
    }

    /// <summary>
    /// Creates the mutation the first time it is called with a slot, and hands the slot its new
    /// options every time after.
    /// </summary>
    /// <remarks>
    /// The mutation counterpart of <see cref="Observe{TKey, TRes}(ref QueryObserver{TKey, TRes}?, QueryOptions{TKey, TRes}, Action)"/>. The slot is the caller's:
    /// a field of the component, disposed with it.
    /// </remarks>
    /// <typeparam name="TParams">The mutation parameters type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="slot">The caller's observer, null until the first call.</param>
    /// <param name="options">The mutation configuration for this render.</param>
    /// <param name="onStateHasChanged">Callback to re-render. Used only when the slot is created.</param>
    /// <returns>The mutation state to read from and call ExecuteAsync on.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public MutationState<TParams, TRes> Observe<TParams, TRes>(
        ref MutationObserver<TParams, TRes>? slot,
        MutationOptions<TParams, TRes> options,
        Action onStateHasChanged)
        where TParams : class
    {
        if (slot is null)
        {
            slot = CreateMutation(options, onStateHasChanged);
        }
        else
        {
            slot.SetOptions(options);
        }

        return slot.State;
    }

    internal void SetObserverOptions<TKey, TRes>(
        QueryObserver<TKey, TRes> observer,
        QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        var options = _defaultOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        QueryWorker<TKey, TRes> worker;
        var moved = false;

        lock (observer.Gate)
        {
            observer.ThrowIfDisposedLocked();

            (QueryState<TKey, TRes> Query, Action Handler)? left = null;

            lock (_gate)
            {
                var state = GetOrCreateQueryLocked(options);

                if (!ReferenceEquals(state, observer.Current))
                {
                    observer.Enabled = options.Enabled;
                    left = observer.Attach(state);
                    moved = true;
                }

                worker = GetOrCreateWorker(state);
            }

            if (left is { } previous) observer.Detach(previous.Query, previous.Handler);
        }

        worker.ApplyOptions(options, observer);

        if (moved) worker.RunIfStale();
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

        foreach (var worker in workers)
        {
            worker.CancelCurrentFetch();
            worker.Dispose();
        }

        if (_ownsEvictionPolicy) _ = _evictionPolicy.StopAsync();
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private (QueryState<TKey, TRes> State, QueryWorker<TKey, TRes> Worker) PrepareRun<TKey, TRes>(
        QueryOptions<TKey, TRes> options) where TKey : ITuple
    {
        lock (_gate)
        {
            var state = GetOrCreateQueryLocked(options);
            return (state, GetOrCreateWorker(state));
        }
    }

    private async Task<(QueryState<TKey, TRes> State, FetchOutcome Outcome)> RunAndReleaseAsync<TKey, TRes>(
        QueryOptions<TKey, TRes> options) where TKey : ITuple
    {
        for (var attempt = 0; attempt < MaxRunAttempts; attempt++)
        {
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

    private QueryWorker<TKey, TRes> GetOrCreateWorker<TKey, TRes>(QueryState<TKey, TRes> state)
        where TKey : ITuple
    {
        ThrowIfDisposedLocked();

        var node = _registry.GetOrCreateNode(state.Key);

        if (node.Worker is QueryWorker<TKey, TRes> existing) return existing;

        var worker = new QueryWorker<TKey, TRes>(
            _defaultOptions, _serviceProvider, state, _persistence);

        worker.OnReleaseDue += () => DisposeWorker(state.Key);
        node.Worker = worker;

        return worker;
    }

    private void WireStateLifecycle<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        state.OnFirstSubscriberAdded += key => _evictionPolicy.CancelEviction(key);
        state.OnLastSubscriberRemoved += unobserved =>
        {
            DisposeWorker(unobserved.Key);
            _evictionPolicy.RegisterForEviction(unobserved.Key, unobserved.CacheOptions);
        };
    }

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
            if (worker is null) return;

            if (!worker.TryRelease()) return;

            node.Worker = null;
        }

        worker.Dispose();
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

        worker?.CancelCurrentFetch();
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
                return;
            }

            if (persisted is null) return;

            state.TryRestore(persisted.Data, persisted.Freshness);
        }
        finally
        {
            state.CompleteRestore();
            NotifyChangedSafely(state);
        }
    }

    private static void NotifyChangedSafely<TKey, TRes>(QueryState<TKey, TRes> state) where TKey : ITuple
    {
        try
        {
            state.NotifyChanged();
        }
        catch
        {
        }
    }

    private static string DescribeKey(ITuple key)
    {
        var segments = new string[key.Length];
        for (var i = 0; i < key.Length; i++) segments[i] = key[i]?.ToString() ?? "null";
        return $"({string.Join(", ", segments)})";
    }
}
