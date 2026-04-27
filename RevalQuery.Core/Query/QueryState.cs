using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query;

public enum QueryStatus
{
    Pending,
    Resolved,
    Exception
}

public enum FetchStatus
{
    Idle,
    Fetching
}

public sealed class QueryState<TKey, TResponse>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> handler,
    FetchOptions? fetchOptions,
    RetryOptions? retryOptions,
    CacheOptions? cacheOptions
)
    : IQueryState<TResponse> where TKey : ITuple
{
    public TKey Key { get; } = key;
    public QueryResult<TResponse>? Result { get; set; }
    public QueryStatus Status { get; set; } = QueryStatus.Pending;
    public FetchStatus FetchStatus { get; set; } = FetchStatus.Idle;
    public Func<QueryHandlerExecutionContext<TKey>, Task<TResponse>> Handler { get; } = handler;
    public FetchOptions? FetchOptions { get; set; } = fetchOptions;
    public RetryOptions? RetryOptions { get; set; } = retryOptions;
    public CacheOptions? CacheOptions { get; set; } = cacheOptions;

    private readonly List<IQueryObserver> _observers = [];
    private DateTimeOffset _lastUpdatedAt = DateTimeOffset.MinValue;

    public event Action? OnChanged;
    public event Action? OnInvalidated;
    public event Action? OnCancelRequested;
    public event Action<QueryState<TKey, TResponse>>? OnLastSubscriberRemoved;
    public event Action<TKey>? OnFirstSubscriberAdded;

    public TResponse? Data => Result is QueryResult<TResponse>.Success s ? s.Value : default;
    public Exception? Error => Result is QueryResult<TResponse>.Failure f ? f.Exception : null;

    public void SetData(TResponse data)
    {
        Result = new QueryResult<TResponse>.Success(data);
        Status = QueryStatus.Resolved;
        _lastUpdatedAt = DateTimeOffset.UtcNow;
        NotifyChanged();
    }

    public void SetError(Exception error)
    {
        Result = new QueryResult<TResponse>.Failure(error);
        Status = QueryStatus.Exception;
        NotifyChanged();
    }

    public bool IsPending => Status == QueryStatus.Pending;
    public bool IsException => Status == QueryStatus.Exception;
    public bool IsResolved => Status == QueryStatus.Resolved;
    public bool IsFetching => FetchStatus == FetchStatus.Fetching;
    public bool IsIdle => FetchStatus == FetchStatus.Idle;
    public bool IsLoading => IsFetching && IsPending;
    public bool IsEnabled => _observers.Count > 0 && _observers.Any(o => o.Enabled);
    public bool CanFetch => FetchStatus == FetchStatus.Idle && IsEnabled;

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

    public void Cancel()
    {
        OnCancelRequested?.Invoke();
    }

    public void Subscribe(IQueryObserver observer)
    {
        if (_observers.Count == 0) OnFirstSubscriberAdded?.Invoke(Key);
        _observers.Add(observer);

    }

    public void Unsubscribe(IQueryObserver observer)
    {
        _observers.Remove(observer);

        if (_observers.Count == 0) OnLastSubscriberRemoved?.Invoke(this);
    }
}