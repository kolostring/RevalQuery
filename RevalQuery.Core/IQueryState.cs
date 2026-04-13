using System;

namespace RevalQuery.Core;

public interface IQueryState
{
    public event Action? OnChanged;
    public event Action? OnInvalidated;

    void IncrementObservers();
    void DecrementObservers();
    void NotifyInvalidated();
}

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