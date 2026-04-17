using System.Runtime.CompilerServices;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Abstractions.Plugin;

/// <summary>
/// Interface for query plugins to hook into the initialization pipeline.
/// Supports middleware-style composition for cross-cutting concerns.
/// </summary>
public interface IQueryPlugin
{
    QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next
    ) where TKey : ITuple;
}