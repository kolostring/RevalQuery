using System;

namespace RevalQuery.Core;

public interface IQueryState<TResponse> : IObservableQueryState
{
    TResponse? Data { get; }
    Exception? Error { get; }

    void SetData(TResponse data);

    bool IsIdle { get; }
    bool IsFetching { get; }
    bool IsLoading { get; }
    bool IsPending { get; }
    bool IsException { get; }
    bool IsResolved { get; }
    bool CanFetch { get; }

    DateTimeOffset LastUpdatedAt { get; }
}