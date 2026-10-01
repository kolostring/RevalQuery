using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query;

/// <summary>
/// Represents the data state of a query.
/// </summary>
/// <remarks>
/// <para>Pending: Query has no data yet (initial or loading).</para>
/// <para>Resolved: Query has data successfully fetched.</para>
/// <para>Exception: Query failed with an error.</para>
/// </remarks>
public enum QueryStatus
{
    /// <summary>Query has no data yet (initial or loading).</summary>
    Pending,
    /// <summary>Query data successfully fetched and available.</summary>
    Resolved,
    /// <summary>Query failed with an error.</summary>
    Exception
}

/// <summary>
/// Represents the network activity state of a query.
/// </summary>
public enum FetchStatus
{
    /// <summary>No fetch operation in progress.</summary>
    Idle,
    /// <summary>Currently executing the query handler.</summary>
    Fetching
}

/// <summary>
/// Represents the complete state of a query including data, status, and options.
/// </summary>
/// <typeparam name="TKey">The key type (ITuple for multi-segment keys).</typeparam>
/// <typeparam name="TResponse">The data type returned by the query.</typeparam>
public sealed class QueryState<TKey, TResponse>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> handler,
    FetchOptions? fetchOptions,
    RetryOptions? retryOptions,
    CacheOptions? cacheOptions
)
    : IQueryState<TResponse> where TKey : ITuple
{
    /// <summary>
    /// The query key that uniquely identifies this query.
    /// </summary>
    public TKey Key { get; } = key;

    /// <summary>
    /// The fetched data. Null when pending or on error.
    /// </summary>
    public TResponse? Data { get; set; }

    /// <summary>
    /// The exception when query failed (Status == QueryStatus.Exception).
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// The data state: Pending, Resolved, or Exception.
    /// </summary>
    public QueryStatus Status { get; set; } = QueryStatus.Pending;

    /// <summary>
    /// The network activity state: Idle or Fetching.
    /// </summary>
    public FetchStatus FetchStatus { get; set; } = FetchStatus.Idle;

    /// <summary>
    /// The async handler function that fetches the data.
    /// Must be a static method.
    /// </summary>
    public Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> Handler { get; } = handler;

    /// <summary>
    /// Per-query fetch options (RefetchInterval, StaleTime).
    /// </summary>
    public FetchOptions? FetchOptions { get; set; } = fetchOptions;

    /// <summary>
    /// Per-query retry options (Retry count, delay calculator).
    /// </summary>
    public RetryOptions? RetryOptions { get; set; } = retryOptions;

    /// <summary>
    /// Per-query cache options (GcTime).
    /// </summary>
    public CacheOptions? CacheOptions { get; set; } = cacheOptions;

    private readonly List<IQueryObserver> _observers = [];
    private readonly object _observersGate = new();
    private readonly object _dataGate = new();
    private DateTimeOffset _lastUpdatedAt = DateTimeOffset.MinValue;
    private bool _hasSettled;
    private bool _isInvalidated;
    private readonly object _restoreGate = new();
    private TaskCompletionSource? _restoreCompletion;
    private Action? _restoreDecider;
    private bool _restoreEnding;

    /// <summary>
    /// Raised when any state property changes (data, status, fetch status).
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// Raised when query is invalidated (cache invalidation triggered).
    /// </summary>
    public event Action? OnInvalidated;

    /// <summary>
    /// Raised when cancellation is requested for current fetch.
    /// </summary>
    public event Action? OnCancelRequested;

    /// <summary>
    /// Raised when the last observer unsubscribes.
    /// </summary>
    public event Action<QueryState<TKey, TResponse>>? OnLastSubscriberRemoved;

    /// <summary>
    /// Raised when the first observer subscribes.
    /// </summary>
    public event Action<TKey>? OnFirstSubscriberAdded;

    /// <summary>
    /// True when query has no data yet (Status == QueryStatus.Pending).
    /// </summary>
    public bool IsPending => Status == QueryStatus.Pending;

    /// <summary>
    /// True when query failed (Status == QueryStatus.Exception).
    /// </summary>
    public bool IsException => Status == QueryStatus.Exception;

    /// <summary>
    /// True when query has data (Status == QueryStatus.Resolved).
    /// </summary>
    public bool IsResolved => Status == QueryStatus.Resolved;

    /// <summary>
    /// True once the query has been invalidated and no fetch has succeeded since.
    /// </summary>
    /// <remarks>
    /// Not derivable from <see cref="LastUpdatedAt"/>. Invalidation leaves the clock alone, so
    /// nothing in the timestamp records that it happened; and even when invalidation did move
    /// the clock to MinValue, that was also what a query that had never fetched read, and a
    /// restore overwrote it with the stored fetch time, which can look perfectly fresh. Without
    /// this flag, an invalidation arriving while a load from persistence was outstanding was
    /// erased by the restore, and a query with no observer to fetch on its behalf at that
    /// moment never refetched at all.
    /// </remarks>
    public bool IsInvalidated
    {
        get { lock (_dataGate) return _isInvalidated; }
    }

    /// <summary>
    /// True when fetch operation is executing (FetchStatus == FetchStatus.Fetching).
    /// </summary>
    public bool IsFetching => FetchStatus == FetchStatus.Fetching;

    /// <summary>
    /// True when no fetch operation in progress (FetchStatus == FetchStatus.Idle).
    /// </summary>
    public bool IsIdle => FetchStatus == FetchStatus.Idle;

    /// <summary>
    /// True while a restore is outstanding: from the query's creation until it knows what
    /// follows the stored data, which is either that the data stands or that a fetch has
    /// started.
    /// </summary>
    /// <remarks>
    /// <para>Read from the restore's completion rather than held as a flag of its own, so
    /// there is one answer to when a restore is over instead of two that stop agreeing.</para>
    /// <para>Deliberately not a FetchStatus value. A restore is not a fetch, and reporting one
    /// as Fetching would turn CanFetch false for its duration and drop any invalidation
    /// arriving meanwhile. See docs/adr/0005 and docs/adr/0006.</para>
    /// </remarks>
    public bool IsRestoring
    {
        get { lock (_restoreGate) return _restoreCompletion is { Task.IsCompleted: false }; }
    }

    /// <summary>
    /// Completes when this query's restore is over, or immediately when it never had one.
    /// </summary>
    public Task RestoreCompleted
    {
        get { lock (_restoreGate) return _restoreCompletion?.Task ?? Task.CompletedTask; }
    }

    /// <summary>
    /// True when the query has no data yet and work is in flight to get some, whether that
    /// work is a fetch or a restore. What a component checks to decide between a spinner and
    /// an empty state.
    /// </summary>
    public bool IsLoading => (IsFetching || IsRestoring) && IsPending;

    /// <summary>
    /// True when at least one enabled observer is subscribed.
    /// Query can fetch only when enabled.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            lock (_observersGate) return _observers.Any(o => o.Enabled);
        }
    }

    /// <summary>
    /// True when at least one observer is subscribed, enabled or not.
    /// A query with no observers is a candidate for eviction.
    /// </summary>
    public bool HasObservers
    {
        get
        {
            lock (_observersGate) return _observers.Count > 0;
        }
    }

    /// <summary>
    /// True when can execute a fetch: Idle AND Enabled.
    /// </summary>
    public bool CanFetch => FetchStatus == FetchStatus.Idle && IsEnabled;

    /// <summary>
    /// Timestamp of last successful data update.
    /// Use with StaleTime to determine if data needs refetching.
    /// </summary>
    /// <remarks>
    /// Read under the same gate that writes it. DateTimeOffset is wider than a machine word, so
    /// an unsynchronised read racing an update can observe half of each and produce a staleness
    /// decision that matches neither.
    /// </remarks>
    public DateTimeOffset LastUpdatedAt
    {
        get { lock (_dataGate) return _lastUpdatedAt; }
    }

    /// <summary>
    /// When this query's data arrived and whether it has been invalidated since, read together
    /// so the pair cannot describe two different moments.
    /// </summary>
    public QueryFreshness Freshness
    {
        get { lock (_dataGate) return new QueryFreshness(_lastUpdatedAt, _isInvalidated); }
    }

    /// <summary>
    /// Whether this query's data should be refetched, given the options in force for it.
    /// </summary>
    /// <remarks>
    /// <para>Staleness is a property of the data, not of whether anything is observing it, so
    /// this answers for any caller: a subscription deciding whether to fetch on mount, and an
    /// imperative <c>QueryAsync</c> deciding whether to serve from cache.</para>
    /// <para>The order of the four questions is the mechanism, not a style choice:</para>
    /// <list type="number">
    /// <item>A query whose clock has never been set is stale, so a static query still fetches
    /// the first time. TanStack's equivalent rung tests for the absence of data rather than of
    /// a timestamp. The two agree everywhere but one path: <see cref="Data"/> is publicly
    /// settable and the optimistic-update pattern writes it without touching the clock, so a
    /// static query holding only an optimistic value is stale here and fresh there. Refetching
    /// an unconfirmed value is the better answer, and the mutation invalidates on settle
    /// anyway.</item>
    /// <item>A static query is otherwise fresh, asked before invalidation so that
    /// <see cref="NotifyInvalidated"/> cannot drag it into a refetch. This is the whole
    /// difference between static and a very long <see cref="CoreFetchOptions.StaleTime"/>.</item>
    /// <item>An invalidated query is stale whatever the clock says.</item>
    /// <item>Otherwise the age of the data is compared against StaleTime.</item>
    /// </list>
    /// <para>Taken under the one lock, so the timestamp and the invalidation flag read here
    /// belong to the same moment rather than to two that a fetch landed between.</para>
    /// </remarks>
    /// <param name="fetchOptions">The query's fetch options with global defaults applied.</param>
    public bool IsStale(CoreFetchOptions fetchOptions)
    {
        lock (_dataGate)
        {
            if (_lastUpdatedAt == DateTimeOffset.MinValue) return true;
            if (fetchOptions.Static) return false;
            if (_isInvalidated) return true;

            return DateTimeOffset.UtcNow - _lastUpdatedAt > fetchOptions.StaleTime;
        }
    }

    /// <summary>
    /// Records the result of a successful fetch: the data, the moment it arrived, and the
    /// resolved status, as one step so a concurrent restore cannot land between them.
    /// </summary>
    /// <param name="data">The fetched data.</param>
    public void ApplyFetched(TResponse data)
    {
        lock (_dataGate)
        {
            Data = data;
            Exception = null;
            Status = QueryStatus.Resolved;
            _lastUpdatedAt = DateTimeOffset.UtcNow;
            _hasSettled = true;

            // Cleared only by a fetch that produced data. A failure leaves the query still
            // holding whatever the invalidation said was out of date.
            _isInvalidated = false;
        }
    }

    /// <summary>
    /// Records a failed fetch: the exception and the status together, so no reader sees one
    /// without the other.
    /// </summary>
    /// <remarks>
    /// Marks the query settled, which is what stops a slow restore from later adopting data
    /// over the failure and leaving the query resolved with an exception still attached.
    /// </remarks>
    /// <param name="exception">The exception the handler produced.</param>
    public void ApplyFailed(Exception exception)
    {
        lock (_dataGate)
        {
            Exception = exception;
            Status = QueryStatus.Exception;
            _hasSettled = true;
        }
    }

    /// <summary>
    /// Adopts data loaded from persistence, keeping the timestamp it was originally fetched at
    /// so restored data is correctly stale rather than appearing fresh. Does nothing once a
    /// fetch has settled, successfully or not.
    /// </summary>
    /// <remarks>
    /// A failed fetch counts as settled. Adopting over one would leave the query resolved with
    /// its exception still set, so a component branching on Exception renders an error beside
    /// data, and QueryAsync returns stale data where it should have thrown.
    /// </remarks>
    /// <param name="data">The restored data.</param>
    /// <param name="freshness">What the stored copy knew about that data's age.</param>
    /// <returns>True when the data was adopted.</returns>
    public bool TryRestore(TResponse data, QueryFreshness freshness)
    {
        lock (_dataGate)
        {
            if (_hasSettled) return false;

            Data = data;
            Exception = null;
            Status = QueryStatus.Resolved;
            _lastUpdatedAt = freshness.LastUpdatedAt;

            // Or-ed, never assigned. An invalidation that arrived while this load was in flight
            // is about the query, not about the copy on disk, and a stored false would erase
            // it: that is the defect ADR 0005 exists for. A stored true adds an invalidation
            // the other side never got to act on.
            _isInvalidated |= freshness.IsInvalidated;

            return true;
        }
    }

    /// <summary>
    /// Opens a restore, so the query reports itself loading rather than empty until it knows
    /// what follows the stored data.
    /// </summary>
    /// <remarks>
    /// Call before starting the read, not from inside it: a query that has already rendered
    /// its empty state before the restore opens shows the blank flash this exists to remove.
    /// </remarks>
    public void BeginRestore()
    {
        lock (_restoreGate)
        {
            _restoreCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Hands the decision about what follows the stored data to a caller, to be made as part
    /// of ending the restore.
    /// </summary>
    /// <remarks>
    /// Returns false when there is no restore outstanding, which is the normal answer for a
    /// query with no persistence behind it and for a subscriber that arrives after the restore
    /// is over. It also returns false once the restore has begun ending, because the decision
    /// it takes then is the last one it will make. Those callers decide for themselves,
    /// immediately.
    /// </remarks>
    /// <param name="decide">
    /// Runs while the restore is still outstanding. A fetch it starts therefore has its fetch
    /// status set before anything reports the restore finished.
    /// </param>
    /// <returns>True when the decision was taken over, so the caller must not also make it.</returns>
    public bool TryDeferUntilRestored(Action decide)
    {
        lock (_restoreGate)
        {
            if (_restoreEnding) return false;
            if (_restoreCompletion is not { Task.IsCompleted: false }) return false;

            _restoreDecider = decide;
            return true;
        }
    }

    /// <summary>
    /// Ends the restore: makes the deferred decision, if one was handed over, and only then
    /// reports the restore finished.
    /// </summary>
    /// <remarks>
    /// The order is the whole point. Ending the restore first would leave an instant with no
    /// restore and no fetch, and an observer notified in that instant renders an empty state
    /// for one frame before the fetch it was about to start turns the spinner back on.
    /// </remarks>
    public void CompleteRestore()
    {
        Action? decide;
        TaskCompletionSource? completion;

        lock (_restoreGate)
        {
            completion = _restoreCompletion;
            if (completion is null || _restoreEnding) return;

            decide = _restoreDecider;
            _restoreDecider = null;

            // Not derivable from the completion, which stays incomplete until the decision
            // below has run. Without it a decision handed over in that window is stored and
            // never made, and a query whose only decision was to fetch never fetches.
            _restoreEnding = true;
        }

        try
        {
            decide?.Invoke();
        }
        catch
        {
            // A decision that throws leaves the query unfetched, which its own caller will
            // see. It must not leave the restore open, because nothing else will ever end it.
        }
        finally
        {
            completion.SetResult();
        }
    }

    /// <summary>
    /// Captures this query's key, data and fetch time as one step, or null when it holds no data.
    /// </summary>
    public QuerySnapshot? Snapshot()
    {
        lock (_dataGate)
        {
            if (Status != QueryStatus.Resolved || Data is null) return null;

            return new QuerySnapshot(
                Key, typeof(TResponse), Data, new QueryFreshness(_lastUpdatedAt, _isInvalidated));
        }
    }

    /// <summary>
    /// Notifies all subscribers that state has changed.
    /// </summary>
    public void NotifyChanged()
    {
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Marks the query invalidated and triggers OnInvalidated. Leaves the data and the moment
    /// it arrived exactly as they were.
    /// </summary>
    /// <remarks>
    /// <para>The mark stays dumb. It records that an invalidation happened and nothing else;
    /// whether that produces a refetch is decided later, by <see cref="IsStale"/>, which is
    /// where a static query is allowed to ignore it.</para>
    /// <para>It does not move <see cref="LastUpdatedAt"/> to MinValue. <see
    /// cref="IsInvalidated"/> already carries the decision, and the clock move corrupted a
    /// public, persisted value: an invalidated query reported data from year one while holding
    /// data from a minute ago. It also cost the staleness decision a state it needs, because
    /// one timestamp cannot mean "never fetched", "invalidated" and "fetched long ago" at
    /// once.</para>
    /// </remarks>
    public void NotifyInvalidated()
    {
        lock (_dataGate) _isInvalidated = true;

        OnInvalidated?.Invoke();
    }

    /// <summary>
    /// Requests cancellation of any in-progress fetch.
    /// </summary>
    public void Cancel()
    {
        OnCancelRequested?.Invoke();
    }

    /// <summary>
    /// Subscribes an observer to this query state.
    /// Raises OnFirstSubscriberAdded if this is the first observer.
    /// </summary>
    public void Subscribe(IQueryObserver observer)
    {
        bool isFirst;
        lock (_observersGate)
        {
            isFirst = _observers.Count == 0;
            _observers.Add(observer);
        }

        if (isFirst) OnFirstSubscriberAdded?.Invoke(Key);
    }

    /// <summary>
    /// Unsubscribes an observer from this query state.
    /// Raises OnLastSubscriberRemoved when all observers are gone.
    /// </summary>
    public void Unsubscribe(IQueryObserver observer)
    {
        bool isLast;
        lock (_observersGate)
        {
            if (!_observers.Remove(observer)) return;
            isLast = _observers.Count == 0;
        }

        if (isLast) OnLastSubscriberRemoved?.Invoke(this);
    }
}