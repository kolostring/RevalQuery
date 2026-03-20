using System.Runtime.CompilerServices;

namespace QueryRevalR;

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