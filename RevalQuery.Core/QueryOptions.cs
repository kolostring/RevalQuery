using System.Runtime.CompilerServices;

namespace RevalQuery.Core;

public static class QueryOptionsFactory
{
    public static QueryOptionsBuilder<TKey, TRes> Create<TKey, TRes>(
        TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<QueryResult<TRes>>> handler)
        where TKey : ITuple
        => new(key, handler);
}

public sealed class QueryOptionsBuilder<TKey, TRes>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<QueryResult<TRes>>> handler)
    where TKey : ITuple
{
    private Func<TRes, Task>? _onSuccess;
    private Func<QueryError, Task>? _onError;
    private Func<QueryResult<TRes>, Task>? _onSettled;
    private bool _enabled = true;
    private FetchOptions _fetchOptions = new();
    private CacheOptions? _cacheOptions;

    public QueryOptionsBuilder<TKey, TRes> OnSuccess(Func<TRes, Task> action)
    {
        _onSuccess = action;
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> OnError(Func<QueryError, Task> action)
    {
        _onError = action;
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> OnSettled(Func<QueryResult<TRes>, Task> action)
    {
        _onSettled = action;
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> Enabled(bool enabled)
    {
        _enabled = enabled;
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> ConfigureFetch(Action<FetchOptionsBuilder> configure)
    {
        var builder = new FetchOptionsBuilder(_fetchOptions);
        configure(builder);
        _fetchOptions = builder.Build();
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> CacheOptions(CacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
        return this;
    }

    public QueryOptions<TKey, TRes> Build() => new(
        key,
        handler,
        _onSuccess,
        _onError,
        _onSettled,
        _enabled,
        _fetchOptions,
        _cacheOptions
    );

    public static implicit operator QueryOptions<TKey, TRes>(QueryOptionsBuilder<TKey, TRes> builder)
        => builder.Build();
}

public sealed record QueryOptions<TKey, TRes>(
    TKey Key,
    Func<QueryHandlerExecutionContext<TKey>, Task<QueryResult<TRes>>> Handler,
    Func<TRes, Task>? OnSuccess = null,
    Func<QueryError, Task>? OnError = null,
    Func<QueryResult<TRes>, Task>? OnSettled = null,
    bool Enabled = true,
    FetchOptions? FetchOptions = null,
    CacheOptions? CacheOptions = null
) where TKey : ITuple;