using System.Runtime.CompilerServices;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Abstractions.Plugin;

/// <summary>
/// Interface for query plugins to hook into the initialization pipeline.
/// Supports middleware-style composition for cross-cutting concerns (validation, logging, metrics).
/// </summary>
public interface IQueryPlugin
{
    /// <summary>
    /// Called during query initialization to transform or validate query options.
    /// Use the `next` parameter to call the next plugin in the chain.
    /// </summary>
    /// <typeparam name="TKey">The query key type.</typeparam>
    /// <typeparam name="TRes">The query response type.</typeparam>
    /// <param name="queryOptions">The current query options.</param>
    /// <param name="next">Delegate to the next plugin in the pipeline.</param>
    /// <returns>Modified query options.</returns>
    QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next
    ) where TKey : ITuple;
}