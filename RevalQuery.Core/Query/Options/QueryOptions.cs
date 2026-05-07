using System.Runtime.CompilerServices;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query.Options;

/// <summary>
/// Immutable query configuration record.
/// </summary>
public sealed record QueryOptions<TKey, TRes>(
    TKey Key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> Handler,
    FetchOptions? FetchOptions = null,
    RetryOptions? RetryOptions = null,
    CacheOptions? CacheOptions = null,
    bool Enabled = true
) where TKey : ITuple;

/// <summary>
/// Factory for creating QueryOptions with fluent builder.
/// </summary>
public abstract class QueryOptions
{
    /// <summary>
    /// Creates a QueryOptionsBuilder for multi-segment keys.
    /// </summary>
    /// <typeparam name="TKey">Key type (ITuple).</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="key">Query key.</param>
    /// <param name="handler">Static async handler.</param>
    public static QueryOptionsBuilder<TKey, TRes> Create<TKey, TRes>(TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler) where TKey : ITuple => new(key, handler);

    /// <summary>
    /// Creates a QueryOptionsBuilder for string keys.
    /// </summary>
    public static QueryOptionsBuilder<ValueTuple<string>, TRes> Create<TRes>(string key,
        Func<QueryHandlerExecutionContext<ValueTuple<string>>, Task<TRes>> handler) => new(ValueTuple.Create(key), handler);
}

/// <summary>
/// Fluent builder for QueryOptions.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TRes">Response type.</typeparam>
public sealed class QueryOptionsBuilder<TKey, TRes>(
    TKey key,
    Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler)
    where TKey : ITuple
{
    private FetchOptions _fetchOptions = new();
    private RetryOptions _retryOptions = new();
    private CacheOptions _cacheOptions = new();
    private bool _enabled = true;

    /// <summary>
    /// Configures fetch options (RefetchInterval, StaleTime).
    /// </summary>
    public QueryOptionsBuilder<TKey, TRes> ConfigureFetch(Action<FetchOptionsBuilder> configure)
    {
        var builder = new FetchOptionsBuilder(_fetchOptions);
        configure(builder);
        _fetchOptions = builder.Build();
        return this;
    }

    /// <summary>
    /// Configures retry options (Retry count, delay).
    /// </summary>
    public QueryOptionsBuilder<TKey, TRes> ConfigureRetry(Action<RetryOptionsBuilder> configure)
    {
        var builder = new RetryOptionsBuilder(_retryOptions);
        configure(builder);
        _retryOptions = builder.Build();
        return this;
    }

    /// <summary>
    /// Configures cache options (GcTime).
    /// </summary>
    public QueryOptionsBuilder<TKey, TRes> ConfigureCache(Action<CacheOptionsBuilder> configure)
    {
        var builder = new CacheOptionsBuilder(_cacheOptions);
        configure(builder);
        _cacheOptions = builder.Build();
        return this;
    }

    /// <summary>
    /// Sets whether the query is initially enabled.
    /// Default: true.
    /// </summary>
    public QueryOptionsBuilder<TKey, TRes> Enabled(bool enabled)
    {
        _enabled = enabled;
        return this;
    }

    /// <summary>
    /// Builds the QueryOptions.
    /// </summary>
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

    /// <summary>
    /// Implicit conversion to QueryOptions.
    /// </summary>
    public static implicit operator QueryOptions<TKey, TRes>(QueryOptionsBuilder<TKey, TRes> builder) => builder.Build();
}