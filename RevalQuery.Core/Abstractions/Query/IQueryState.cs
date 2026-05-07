namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Base interface for query state management.
/// Defines common properties, events, and lifecycle methods for queries.
/// </summary>
public interface IQueryState : IObservableQueryState
{
    /// <summary>
    /// True when no fetch operation is in progress (FetchStatus.Idle).
    /// </summary>
    bool IsIdle { get; }

    /// <summary>
    /// True when a fetch operation is currently executing (FetchStatus.Fetching).
    /// </summary>
    bool IsFetching { get; }

    /// <summary>
    /// True when at least one enabled observer is subscribed to this query.
    /// A query can fetch only when enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// True when fetching AND pending (no data yet) - equivalent to loading state.
    /// </summary>
    bool IsLoading { get; }

    /// <summary>
    /// True when query has no data yet (QueryStatus.Pending).
    /// </summary>
    bool IsPending { get; }

    /// <summary>
    /// True when query failed (QueryStatus.Exception).
    /// </summary>
    bool IsException { get; }

    /// <summary>
    /// True when query has data successfully (QueryStatus.Resolved).
    /// </summary>
    bool IsResolved { get; }

    /// <summary>
    /// True when query can execute a fetch: FetchStatus.Idle AND IsEnabled.
    /// </summary>
    bool CanFetch { get; }

    /// <summary>
    /// Timestamp when the query data was last successfully fetched or updated.
    /// Use with StaleTime to determine if data needs refetching.
    /// </summary>
    DateTimeOffset LastUpdatedAt { get; }

    /// <summary>
    /// Raised when the query is invalidated (e.g., cache invalidation triggered).
    /// Triggers automatic refetch if query is enabled.
    /// </summary>
    event Action? OnInvalidated;

    /// <summary>
    /// Raised when a cancellation is requested for the current fetch operation.
    /// </summary>
    event Action? OnCancelRequested;

    /// <summary>
    /// Notifies that the query has been invalidated.
    /// Triggers OnInvalidated event and marks data as stale.
    /// </summary>
    void NotifyInvalidated();

    /// <summary>
    /// Requests cancellation of any in-progress fetch operation.
    /// </summary>
    void Cancel();
}

/// <summary>
/// Generic query state interface with typed data access.
/// </summary>
/// <typeparam name="TData">The type of data returned by the query.</typeparam>
public interface IQueryState<TData> : IQueryState
{
    /// <summary>
    /// The query result data. Null when pending or on error (unless previously cached).
    /// </summary>
    TData? Data { get; set; }

    /// <summary>
    /// The exception if the query failed (QueryStatus.Exception).
    /// </summary>
    Exception? Exception { get; set; }
}