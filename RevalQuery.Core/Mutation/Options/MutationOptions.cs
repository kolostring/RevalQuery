using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Execution;

namespace RevalQuery.Core.Mutation.Options;

public sealed record MutationOptions<TParams, TRes>(
    Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> Handler,
    RetryOptions? Retry = null
) where TParams : class;