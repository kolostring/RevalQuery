using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Execution;

namespace RevalQuery.Core.Mutation.Options;

/// <summary>
/// Configuration for a mutation (write operation).
/// </summary>
/// <typeparam name="TParams">The parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed record MutationOptions<TParams, TRes>(
    Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> Handler,
    RetryOptions? Retry = null,
    Func<TParams, Task>? OnMutate = null,
    Func<TRes, TParams, Task>? OnResolved = null,
    Func<Exception, TParams, Task>? OnException = null,
    Func<TRes?, Exception?, TParams, Task>? OnSettled = null
) where TParams : class;