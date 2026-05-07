using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Plugin;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Plugin;

/// <summary>
/// Built-in plugin that validates query handlers are static methods.
/// Ensures purity - handlers must not capture instance state.
/// </summary>
public class QueryPluginHandlersStatelessValidation : IQueryPlugin
{
    /// <summary>
    /// Validates that the handler is a static method.
    /// Throws if handler is an instance method or lambda.
    /// </summary>
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