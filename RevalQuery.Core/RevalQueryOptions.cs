namespace RevalQuery.Core;

public class RevalQueryOptions
{
    public QueryPluginsPipeline QueryPluginsPipeline { get; set; } = new([]);

    public CacheOptions CacheOptions { get; set; } = CacheOptions.Default;
    public CoreFetchOptions FetchOptions { get; set; } = CoreFetchOptions.Default;
}