using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Plugin.Pipeline;

namespace RevalQuery.Core.Configuration;

/// <summary>
/// Configuration options for the entire RevalQuery library.
/// Centralizes default settings for caching, fetching, and plugins.
/// </summary>
public class RevalQueryOptions
{
    public QueryPluginsPipeline QueryPluginsPipeline { get; set; } = new([]);

    public CoreCacheOptions CacheOptions { get; set; } = CoreCacheOptions.Default;
    public CoreFetchOptions FetchOptions { get; set; } = CoreFetchOptions.Default;
    public CoreRetryOptions RetryOptions { get; set; } = CoreRetryOptions.QueryDefault;
}