namespace QueryRevalR.Core;

public static class MutationOptionsFactory
{
    public static MutationOptions<TInput, TRes> Create<TInput, TRes>(
        Func<MutationHandlerExecutionContext<TInput>, Task<QueryResult<TRes>>> handler)
        => new(handler);
}

public sealed record MutationOptions<TInput, TRes>(
    Func<MutationHandlerExecutionContext<TInput>, Task<QueryResult<TRes>>> Handler
);