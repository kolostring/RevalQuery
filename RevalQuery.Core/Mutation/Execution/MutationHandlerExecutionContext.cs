namespace RevalQuery.Core.Mutation.Execution;

/// <summary>
/// Context passed to mutation handlers with params, services, and cancellation.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
public sealed class MutationHandlerExecutionContext<TParams>
{
    /// <summary>
    /// The parameters passed to the mutation.
    /// </summary>
    public required TParams Params { get; init; }

    /// <summary>
    /// Service provider for resolving dependencies.
    /// </summary>
    public required IServiceProvider ServiceProvider { get; init; }

    /// <summary>
    /// Cancellation token for the current mutation operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}