using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Query.Execution;

public sealed class QueryHandlerExecutionContext<TKey> where TKey : ITuple
{
    public required TKey Key { get; init; }
    public required IServiceProvider ServiceProvider { get; init; }
    public CancellationToken? CancellationToken { get; init; }
}