using System.Runtime.CompilerServices;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query.Options;

public sealed record QueryOptions<TKey, TRes>(
    TKey Key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> Handler,
    FetchOptions? FetchOptions = null,
    CacheOptions? CacheOptions = null
) where TKey : ITuple;

public abstract class QueryOptions
{
    public static QueryOptionsBuilder<TKey, TRes> Create<TKey, TRes>(TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler) where TKey : ITuple => new(key, handler);
}

public sealed class QueryOptionsBuilder<TKey, TRes>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler)
    where TKey : ITuple
{
    private bool _enabled = true;
    private FetchOptions _fetchOptions = new();
    private CacheOptions? _cacheOptions;

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
        _fetchOptions,
        _cacheOptions
    );

    public static implicit operator QueryOptions<TKey, TRes>(QueryOptionsBuilder<TKey, TRes> builder)
        => builder.Build();
}