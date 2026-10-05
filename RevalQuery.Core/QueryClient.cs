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
using RevalQuery.Core.Scope;

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
    /// Creates a scope: the place one component reads its queries and mutations through, so it
    /// holds no observers of its own.
    /// </summary>
    /// <remarks>
    /// The caller disposes the scope with the component. A framework adapter attaches a host to
    /// it; see <see cref="QueryScope"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public QueryScope CreateScope()
    {
        lock (_gate) ThrowIfDisposedLocked();

        return new QueryScope(this);
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

        // Registered before anything is awaited, so a key already taken by another result type
        // is reported on the first task this method returns rather than discovered later by
        // whoever happens to subscribe. This method is async, so that report is a faulted task
        // and not a throw from the call: a caller discarding the task sees it only through the
        // catch they write around the discard.
        var (state, _) = PrepareRun(options);

        // Checked here rather than left to the WaitAsync below, which only sees a token
        // cancelled while a fetch is actually outstanding. A caller who has already walked
        // away must not be handed data because the registry happened to be warm, or because
        // the handler happened to complete synchronously. The release is the same one both
        // exits below do: PrepareRun registered a worker and nothing has subscribed to it.
        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseIfUnobserved(state);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Judged against what the query holds now. An outstanding restore is deliberately not
        // waited for: ADR 0006 has restores never hold a fetch up, and an adapter that is slow
        // or wedged would otherwise hang every imperative caller behind it. The race is already
        // handled at the other end, where TryRestore refuses to overwrite a fetch that landed
        // first. The cost is that stored data misses the freshness test on the very first call
        // for a key, and that call fetches instead.
        if (!state.IsStale(_defaultOptions.FetchOptions.Apply(options.FetchOptions)))
        {
            // Nothing subscribed here, so the query goes back on the eviction list exactly as
            // it would after a fetch.
            ReleaseIfUnobserved(state);
            return state.Data!;
        }

        // WaitAsync rather than a token passed into the run: the fetch is shared with every
        // other caller joined to it, and one caller walking away must not take it from them.
        var (ran, outcome) = await RunAndReleaseAsync(options).WaitAsync(cancellationToken);

        // Checked before the exception, which belongs to the query rather than to this call.
        // A fetch cancelled while an older failure still sat on the state would otherwise
        // throw that failure, reporting an error this call never hit.
        if (outcome == FetchOutcome.Cancelled)
        {
            // Returning Data here would hand back null, or another caller's older result, as
            // though this call had fetched it. The caller asked for data and there is none.
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

        // Registering the query, subscribing to it and giving it a worker happen together.
        // Split apart, an eviction landing in the gap would drop the state a component is
        // about to render, and the next subscriber to the same key would get a second one.
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

        // Subscribing to a query somebody else created has to adopt these options, not inherit
        // theirs. Without this a component only reaches SetOptions on its second render, so
        // until then it runs on whatever a route loader or an earlier component asked for --
        // and a NeverStale() loader would leave it permanently unrefreshable.
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
    /// a <see cref="QueryScope"/> to do it for them. The slot is the caller's: a field of the
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

    /// <summary>
    /// Points an observer at the query its new options name: the one it is on when the key is
    /// unchanged, another when it is not.
    /// </summary>
    /// <remarks>
    /// <para>Called on every render, so the same-key path is the common one and must stay
    /// cheap: it adopts the options and does nothing else unless that made the query newly
    /// enabled or changed its polling interval, both of which the worker decides.</para>
    /// <para>A move resolves the new query before anything is released, so a result type clash
    /// throws with the observer exactly where it was. It attaches to the new query in the same
    /// lock hold that finds it, for the reason <see cref="Subscribe"/> does everything in one:
    /// an eviction landing in a gap would drop the state the observer is about to report. Only
    /// then is the old query released, the same release a disposal makes. The observer is
    /// briefly subscribed to both, which costs nothing: it forwards notifications from the new
    /// one alone.</para>
    /// <para>Does not cancel a fetch the old query has in flight. Releasing its last observer
    /// stops its polling and puts it on the eviction list, and its worker, which cannot be
    /// disposed under a running fetch, is let go once the fetch settles.</para>
    /// <para>An observer lock is taken first and the registry's second, and never the other
    /// way round, so a switch and a disposal of the same observer cannot interleave.</para>
    /// </remarks>
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
                // Throws for a disposed client and for a clash of result types, in both cases
                // before anything has been attached or detached.
                var state = GetOrCreateQueryLocked(options);

                if (!ReferenceEquals(state, observer.Current))
                {
                    // Set before attaching so the query reads as enabled the moment it has
                    // an observer. The worker starts polling from its own state, not from
                    // noticing this as a toggle.
                    observer.Enabled = options.Enabled;
                    left = observer.Attach(state);
                    moved = true;
                }

                // Also the still-in-registry check for an unchanged key: the state came from
                // the registry a line ago, so a worker made for it has an owner. An observer
                // whose state was evicted, which cannot happen while it is subscribed to it,
                // would have found a fresh state here and moved to it.
                worker = GetOrCreateWorker(state);
            }

            if (left is { } previous) observer.Detach(previous.Query, previous.Handler);
        }

        worker.ApplyOptions(options, observer);

        // A new subscription fetches if its data is stale. An unchanged key does not: the
        // worker has already decided whether turning the query on warrants one.
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

        // Cancelled before being disposed, which disposal alone no longer does. The scope that
        // owns this client is ending, so a handler still running is about to reach for services
        // that are going away, and no caller is left to receive what it produces.
        foreach (var worker in workers)
        {
            worker.CancelCurrentFetch();
            worker.Dispose();
        }

        // Only stop a policy this client created. An injected one is the container's to dispose.
        if (_ownsEvictionPolicy) _ = _evictionPolicy.StopAsync();
    }

    /// <summary>
    /// Refuses work that would put anything back into a registry this client has already
    /// emptied. Callers hold the lock, so the answer cannot change under them.
    /// </summary>
    /// <remarks>
    /// Guards the entry points a live render reaches: Subscribe, QueryAsync,
    /// GetOrCreateQuery, SetOptions and CreateMutation. A component rendering against a
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

        // A release the worker refused because it was fetching is retried here, once that
        // fetch has settled.
        worker.OnReleaseDue += () => DisposeWorker(state.Key);
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
    /// Used after a QueryAsync call, which creates no observer of its own.
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
            if (worker is null) return;

            // A worker with a fetch in flight keeps its node. Clearing it would leave that
            // fetch running with nothing pointing at it: CancelAsync could no longer reach it,
            // and the next caller would be handed a second worker that fetched alongside it.
            // The worker asks again once its fetch settles.
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

        // Cancelled, unlike the release above. The state is leaving the registry, so a fetch
        // still running has nowhere left to land, and letting it finish would write data to
        // persistence for a query the cache has already forgotten.
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
            state.TryRestore(persisted.Data, persisted.Freshness);
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
