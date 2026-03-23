using System.Runtime.CompilerServices;

namespace QueryRevalR.Core;

public interface IQueryPlugin
{
    QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next)
        where TKey : ITuple;
}