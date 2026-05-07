using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Plugin;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Plugin.Pipeline;

/// <summary>
/// Manages the plugin pipeline for query initialization.
/// Uses middleware pattern - each plugin can transform or validate options.
/// </summary>
public sealed class QueryPluginsPipeline(IEnumerable<IQueryPlugin>? initialPlugins = null)
{
    private readonly List<IQueryPlugin> _plugins = new(initialPlugins ?? []);

    /// <summary>
    /// Adds a plugin to the end of the pipeline.
    /// Plugins execute in registration order.
    /// </summary>
    public void Add(IQueryPlugin plugin)
    {
        _plugins.Add(plugin);
    }

    /// <summary>
    /// Processes query options through all plugins in chain.
    /// Called internally by QueryClient when subscribing/fetching.
    /// </summary>
    public QueryOptions<TKey, TRes> HandleQueryOptions<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> pipeline = (opt) => opt;
        foreach (var plugin in _plugins.AsEnumerable().Reverse())
        {
            var localNext = pipeline;
            pipeline = (opt) => plugin.OnQueryInitialize(opt, localNext);
        }

        return pipeline(queryOptions);
    }
}