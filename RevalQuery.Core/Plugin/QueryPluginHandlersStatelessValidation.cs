using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Plugin;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Plugin;

/// <summary>
/// Plugin that validates query handler signatures.
/// Ensures handlers are properly configured before execution.
/// </summary>
public class QueryPluginHandlersStatelessValidation : IQueryPlugin
{
    public QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next
    ) where TKey : ITuple
    {
        if (queryOptions.Handler == null) throw new InvalidOperationException("Query handler cannot be null");

        if (!queryOptions.Handler.Method.IsStatic)
            throw new InvalidOperationException(
                $"Query handler for key {queryOptions.Key} must be a static method to ensure stateless execution. " +
                "Instance methods or closures are not allowed.");

        return next(queryOptions);
    }
}