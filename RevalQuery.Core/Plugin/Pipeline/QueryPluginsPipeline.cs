using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Plugin;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Plugin.Pipeline;

/// <summary>
/// Manages the plugin pipeline for query initialization.
/// Uses middleware pattern for composable behavior.
/// </summary>
public sealed class QueryPluginsPipeline(IEnumerable<IQueryPlugin> plugins)
{
    public QueryOptions<TKey, TRes> HandleQueryOptions<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions)
        where TKey : ITuple
    {
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> pipeline = (opt) => opt;
        foreach (var plugin in plugins.Reverse())
        {
            var localNext = pipeline;
            pipeline = (opt) => plugin.OnQueryInitialize(opt, localNext);
        }

        return pipeline(queryOptions);
    }
}