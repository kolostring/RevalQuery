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
    private DateTimeOffset _lastUpdatedAt = DateTimeOffset.MinValue;

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
    /// True when fetch operation is executing (FetchStatus == FetchStatus.Fetching).
    /// </summary>
    public bool IsFetching => FetchStatus == FetchStatus.Fetching;

    /// <summary>
    /// True when no fetch operation in progress (FetchStatus == FetchStatus.Idle).
    /// </summary>
    public bool IsIdle => FetchStatus == FetchStatus.Idle;

    /// <summary>
    /// True when fetching AND pending - shows loading state.
    /// </summary>
    public bool IsLoading => IsFetching && IsPending;

    /// <summary>
    /// True when at least one enabled observer is subscribed.
    /// Query can fetch only when enabled.
    /// </summary>
    public bool IsEnabled => _observers.Count > 0 && _observers.Any(o => o.Enabled);

    /// <summary>
    /// True when can execute a fetch: Idle AND Enabled.
    /// </summary>
    public bool CanFetch => FetchStatus == FetchStatus.Idle && IsEnabled;

    /// <summary>
    /// Timestamp of last successful data update.
    /// Use with StaleTime to determine if data needs refetching.
    /// </summary>
    public DateTimeOffset LastUpdatedAt => _lastUpdatedAt;

    /// <summary>
    /// Marks the query data as stale (needs refetching).
    /// </summary>
    public void SetStale()
    {
        _lastUpdatedAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Marks the query data as fresh (successfully fetched).
    /// </summary>
    public void SetFresh()
    {
        _lastUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Notifies all subscribers that state has changed.
    /// </summary>
    public void NotifyChanged()
    {
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Notifies that the query has been invalidated.
    /// Sets LastUpdatedAt to MinValue and triggers OnInvalidated.
    /// </summary>
    public void NotifyInvalidated()
    {
        _lastUpdatedAt = DateTimeOffset.MinValue;
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
        if (_observers.Count == 0) OnFirstSubscriberAdded?.Invoke(Key);
        _observers.Add(observer);

    }

    /// <summary>
    /// Unsubscribes an observer from this query state.
    /// Raises OnLastSubscriberRemoved when all observers are gone.
    /// </summary>
    public void Unsubscribe(IQueryObserver observer)
    {
        _observers.Remove(observer);

        if (_observers.Count == 0) OnLastSubscriberRemoved?.Invoke(this);
    }
}