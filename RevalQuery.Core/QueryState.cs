using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace RevalQuery.Core;

public enum QueryStatus
{
    Idle,
    Fetching
}

public sealed class QueryState<TKey, TResponse>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<QueryResult<TResponse>>> handler,
    CacheOptions cacheOptions,
    IServiceProvider serviceProvider
)
    : IQueryState<TResponse> where TKey : ITuple
{
    public TKey Key { get; } = key;
    private QueryResult<TResponse>? _result;
    private QueryStatus _status = QueryStatus.Idle;

    private int _observersCount = 0;
    private DateTimeOffset _lastUpdatedAt = DateTimeOffset.MinValue;
    private CacheOptions _cacheOptions = cacheOptions;

    public event Action? OnChanged;
    public event Action? OnInvalidated;
    public event Action<TKey, CacheOptions>? OnLastSubscriberRemoved;
    public event Action<TKey>? OnFirstSubscriberAdded;

    public TResponse? Data => _result is QueryResult<TResponse>.Success s ? s.Value : default;
    public QueryError? Error => _result is QueryResult<TResponse>.Failure f ? f.Error : null;

    public QueryResult<TResponse>? Res
    {
        get => _result;
    }

    public void SetData(TResponse data)
    {
        _result = new QueryResult<TResponse>.Success(data);
        _status = QueryStatus.Idle;
        _lastUpdatedAt = DateTimeOffset.UtcNow;
        NotifyChanged();
    }

    public void SetCacheOptions(CacheOptions cacheOptions)
    {
        if (cacheOptions.GcTime > _cacheOptions.GcTime)
        {
            _cacheOptions = cacheOptions;
        }
    }

    public bool IsIdle => _status == QueryStatus.Idle;
    public bool IsFetching => _status == QueryStatus.Fetching;
    public bool IsPending => _result == null;
    public bool IsLoading => IsFetching && IsPending;
    public bool IsError => IsIdle && _result is QueryResult<TResponse>.Failure;
    public bool IsSuccess => IsIdle && _result is QueryResult<TResponse>.Success;
    public bool CanFetch => IsIdle && _observersCount > 0;

    public DateTimeOffset LastUpdatedAt => _lastUpdatedAt;

    public async Task Run(CancellationToken ct)
    {
        if (IsFetching)
        {
            return;
        }

        _status = QueryStatus.Fetching;
        NotifyChanged();

        try
        {
            var ctx = new QueryHandlerExecutionContext<TKey>()
            {
                Key = Key,
                ServiceProvider = serviceProvider,
                CancellationToken = ct
            };
            _result = await handler(ctx);
            _lastUpdatedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _status = QueryStatus.Idle;
        }

        NotifyChanged();
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    public void NotifyInvalidated()
    {
        _lastUpdatedAt = DateTimeOffset.MinValue;
        OnInvalidated?.Invoke();
    }

    public void IncrementObservers()
    {
        if (_observersCount == 0)
        {
            OnFirstSubscriberAdded?.Invoke(Key);
        }

        _observersCount++;
    }

    public void DecrementObservers()
    {
        _observersCount--;
        if (_observersCount == 0)
        {
            OnLastSubscriberRemoved?.Invoke(Key, _cacheOptions);
        }

        if (_observersCount < 0)
        {
            throw new InvalidOperationException(
                $"Query state with key {Key} has invalid observer count: {_observersCount}.");
        }
    }
}