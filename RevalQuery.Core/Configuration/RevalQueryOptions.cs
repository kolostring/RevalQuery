using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Plugin.Pipeline;

namespace RevalQuery.Core.Configuration;

/// <summary>
/// Configuration options for the entire RevalQuery library.
/// Centralizes default settings for caching, fetching, plugins, and retry.
/// </summary>
public class RevalQueryOptions
{
    /// <summary>
    /// Pipeline for query initialization plugins.
    /// Add validation, logging, or metrics plugins here.
    /// </summary>
    public QueryPluginsPipeline QueryPluginsPipeline { get; set; } = new([]);

    /// <summary>
    /// Default cache options for all queries.
    /// </summary>
    public CoreCacheOptions CacheOptions { get; set; } = CoreCacheOptions.Default;

    /// <summary>
    /// Default fetch options for all queries.
    /// </summary>
    public CoreFetchOptions FetchOptions { get; set; } = CoreFetchOptions.Default;

    /// <summary>
    /// Default retry options for all queries and mutations.
    /// </summary>
    public CoreRetryOptions RetryOptions { get; set; } = CoreRetryOptions.QueryDefault;

    /// <summary>
    /// The clock the library reads the current time from, for staleness and for the
    /// timestamp a fetch is persisted with. Defaults to <see cref="TimeProvider.System"/>;
    /// supply a fake one, such as <c>FakeTimeProvider</c>, to drive time from a test.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}