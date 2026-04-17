namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Base interface for query state.
/// Defines common lifecycle and notification events.
/// </summary>
public interface IQueryState
{
    event Action? OnInvalidated;

    void NotifyInvalidated();
}

/// <summary>
/// Generic query state interface with data access.
/// </summary>
public interface IQueryState<TData> : IQueryState
{
    TData? Data { get; }
    Exception? Error { get; }

    void SetData(TData data);

    bool IsIdle { get; }
    bool IsFetching { get; }
    bool IsLoading { get; }
    bool IsPending { get; }
    bool IsException { get; }
    bool IsResolved { get; }
    bool CanFetch { get; }

    DateTimeOffset LastUpdatedAt { get; }
}