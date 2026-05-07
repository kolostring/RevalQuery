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
}