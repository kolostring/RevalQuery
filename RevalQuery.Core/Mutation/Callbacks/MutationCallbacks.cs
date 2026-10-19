namespace RevalQuery.Core.Mutation.Callbacks;

/// <summary>
/// Per-call mutation callbacks - passed to ExecuteAsync() for handling outcomes.
/// </summary>
/// <typeparam name="TParams">Parameters type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public sealed record MutateOptions<TParams, TResponse>(
    Func<MutationResolvedContext<TParams, TResponse>, Task>? OnResolved = null,
    Func<MutationExceptionContext<TParams>, Task>? OnException = null,
    Func<MutationSettledContext<TParams, TResponse>, Task>? OnSettled = null
);