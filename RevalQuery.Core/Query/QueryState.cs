using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query;

public enum QueryStatus
{
    Idle,
    Fetching
}

/// <summary>
/// Represents the state of a query including data, status, and lifecycle.
/// Thread-safe with immutable initialization.
/// </summary>
public sealed class QueryState<TKey, TResponse>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> handler,
    FetchOptions? fetchOptions,
    RetryOptions? retryOptions,
    CacheOptions? cacheOptions
)
    : IQueryState<TResponse>, IObservableQueryState where TKey : ITuple
{
    public TKey Key { get; } = key;
    public QueryResult<TResponse>? Result { get; set; }
    public QueryStatus Status { get; set; } = QueryStatus.Idle;
    public Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> Handler { get; } = handler;
    public FetchOptions? FetchOptions { get; set; } = fetchOptions;
    public RetryOptions? RetryOptions { get; set; } = retryOptions;
    public CacheOptions? CacheOptions { get; set; } = cacheOptions;

    private int _observersCount;
    private DateTimeOffset _lastUpdatedAt = DateTimeOffset.MinValue;

    public event Action? OnChanged;
    public event Action? OnInvalidated;
    public event Action<QueryState<TKey, TResponse>>? OnLastSubscriberRemoved;
    public event Action<TKey>? OnFirstSubscriberAdded;

    public TResponse? Data => Result is QueryResult<TResponse>.Success s ? s.Value : default;
    public Exception? Error => Result is QueryResult<TResponse>.Failure f ? f.Exception : null;

    public void SetData(TResponse data)
    {
        Result = new QueryResult<TResponse>.Success(data);
        Status = QueryStatus.Idle;
        _lastUpdatedAt = DateTimeOffset.UtcNow;
        NotifyChanged();
    }

    public bool IsIdle => Status == QueryStatus.Idle;
    public bool IsFetching => Status == QueryStatus.Fetching;
    public bool IsPending => Result == null;
    public bool IsLoading => IsFetching && IsPending;
    public bool IsException => IsIdle && Result is QueryResult<TResponse>.Failure;
    public bool IsResolved => IsIdle && Result is QueryResult<TResponse>.Success;
    public bool CanFetch => IsIdle && _observersCount > 0;

    public DateTimeOffset LastUpdatedAt => _lastUpdatedAt;

    public void SetStale()
    {
        _lastUpdatedAt = DateTimeOffset.MinValue;
    }

    public void SetFresh()
    {
        _lastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void NotifyChanged()
    {
        OnChanged?.Invoke();
    }

    public void NotifyInvalidated()
    {
        _lastUpdatedAt = DateTimeOffset.MinValue;
        OnInvalidated?.Invoke();
    }

    public void IncrementObservers()
    {
        if (_observersCount == 0) OnFirstSubscriberAdded?.Invoke(Key);

        _observersCount++;
    }

    public void DecrementObservers()
    {
        _observersCount--;
        if (_observersCount == 0) OnLastSubscriberRemoved?.Invoke(this);

        if (_observersCount < 0)
            throw new InvalidOperationException(
                $"Query state with key {Key} has invalid observer count: {_observersCount}.");
    }
}