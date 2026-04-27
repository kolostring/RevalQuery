namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Base interface for query state.
/// Defines common lifecycle and notification events.
/// </summary>
public interface IQueryState
{

    bool IsIdle { get; }
    bool IsFetching { get; }
    bool IsEnabled { get; }
    bool IsLoading { get; }
    bool IsPending { get; }
    bool IsException { get; }
    bool IsResolved { get; }
    bool CanFetch { get; }

    DateTimeOffset LastUpdatedAt { get; }

    event Action? OnInvalidated;
    event Action? OnCancelRequested;

    void NotifyInvalidated();
    void Cancel();
}

/// <summary>
/// Generic query state interface with data access.
/// </summary>
public interface IQueryState<TData> : IQueryState, IObservableQueryState
{
    TData? Data { get; set; }
    Exception? Exception { get; set; }
}