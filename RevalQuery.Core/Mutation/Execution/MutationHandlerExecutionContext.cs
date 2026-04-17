namespace RevalQuery.Core.Mutation.Execution;

public sealed class MutationHandlerExecutionContext<TParams>
{
    public required TParams Params { get; init; }
    public required IServiceProvider ServiceProvider { get; init; }
    public CancellationToken CancellationToken { get; init; }
}