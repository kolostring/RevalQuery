namespace RevalQuery.Core.Mutation.Callbacks;

public sealed record MutateOptions<TParams, TResponse>(
    Func<TResponse, TParams, Task>? OnResolved = null,
    Func<Exception, TParams, Task>? OnException = null,
    Func<TResponse?, Exception?, TParams, Task>? OnSettled = null
);