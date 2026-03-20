using System.Reflection;
using System.Runtime.CompilerServices;

namespace QueryRevalR;

public sealed class QueryPluginHandlersStatelessValidation : IQueryPlugin
{
    public QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(QueryOptions<TKey, TRes> queryOptions,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next)
        where TKey : ITuple
    {
        var target = queryOptions.Handler.Target;

        var targetType = target?.GetType();

        var isStateful = targetType?.GetFields(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
            )
            .Length is > 0;

        if (isStateful)
        {
            throw new InvalidOperationException(
                $"Client Query Handler for key {queryOptions.Key.ToString()} must be a 'static' lambda. " +
                $"Current target is stateful: {targetType!.Name}. " +
                $"Putting 'static' on the handler callback might help identify where is referencing an external state.");
        }

        var newQueryOptions = next(queryOptions);
        return newQueryOptions;
    }
}