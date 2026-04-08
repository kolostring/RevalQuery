namespace RevalQuery.Core;

public sealed record MutationOptions<TInput, TRes>(
    Func<MutationHandlerExecutionContext<TInput>, Task<QueryResult<TRes>>> Handler
);