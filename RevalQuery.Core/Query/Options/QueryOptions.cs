using System.Runtime.CompilerServices;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query.Options;

public sealed record QueryOptions<TKey, TRes>(
    TKey Key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> Handler,
    FetchOptions? FetchOptions = null,
    RetryOptions? RetryOptions = null,
    CacheOptions? CacheOptions = null,
    bool Enabled = true
) where TKey : ITuple;

public abstract class QueryOptions
{
    public static QueryOptionsBuilder<TKey, TRes> Create<TKey, TRes>(TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler) where TKey : ITuple => new(key, handler);

    public static QueryOptionsBuilder<ValueTuple<string>, TRes> Create<TRes>(string key,
        Func<QueryHandlerExecutionContext<ValueTuple<string>>, Task<TRes>> handler) => new(ValueTuple.Create(key), handler);
}

public sealed class QueryOptionsBuilder<TKey, TRes>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler)
    where TKey : ITuple
{
    private FetchOptions _fetchOptions = new();
    private RetryOptions _retryOptions = new();
    private CacheOptions _cacheOptions = new();
    private bool _enabled = true;

    public QueryOptionsBuilder<TKey, TRes> ConfigureFetch(Action<FetchOptionsBuilder> configure)
    {
        var builder = new FetchOptionsBuilder(_fetchOptions);
        configure(builder);
        _fetchOptions = builder.Build();
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> ConfigureRetry(Action<RetryOptionsBuilder> configure)
    {
        var builder = new RetryOptionsBuilder(_retryOptions);
        configure(builder);
        _retryOptions = builder.Build();
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> ConfigureCache(Action<CacheOptionsBuilder> configure)
    {
        var builder = new CacheOptionsBuilder(_cacheOptions);
        configure(builder);
        _cacheOptions = builder.Build();
        return this;
    }

    public QueryOptionsBuilder<TKey, TRes> Enabled(bool enabled)
    {
        _enabled = enabled;
        return this;
    }

    public QueryOptions<TKey, TRes> Build()
    {
        return new QueryOptions<TKey, TRes>(
            key,
            handler,
            _fetchOptions,
            _retryOptions,
            _cacheOptions,
            _enabled
        );
    }

    public static implicit operator QueryOptions<TKey, TRes>(QueryOptionsBuilder<TKey, TRes> builder) => builder.Build();
}