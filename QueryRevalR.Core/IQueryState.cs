namespace QueryRevalR;

public interface IQueryState<TResponse> : IObservableQueryState
{
    TResponse? Data { get; }
    QueryError? Error { get; }

    void SetData(TResponse data);

    bool IsIdle { get; }
    bool IsFetching { get; }
    bool IsLoading { get; }
    bool IsPending { get; }
    bool IsError { get; }
    bool IsSuccess { get; }
    bool CanFetch { get; }

    DateTimeOffset LastUpdatedAt { get; }
}