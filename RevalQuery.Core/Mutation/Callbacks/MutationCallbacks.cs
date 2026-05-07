namespace RevalQuery.Core.Mutation.Callbacks;

/// <summary>
/// Per-call mutation callbacks - passed to ExecuteAsync() for handling outcomes.
/// </summary>
/// <typeparam name="TParams">Parameters type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public sealed record MutateOptions<TParams, TResponse>(
    Func<TResponse, TParams, Task>? OnResolved = null,
    Func<Exception, TParams, Task>? OnException = null,
    Func<TResponse?, Exception?, TParams, Task>? OnSettled = null
);