using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Query.Execution;

/// <summary>
/// Context passed to query handlers with key, services, and cancellation.
/// </summary>
/// <typeparam name="TKey">The query key type.</typeparam>
public sealed class QueryHandlerExecutionContext<TKey> where TKey : ITuple
{
    /// <summary>
    /// The query key (for multi-key scenarios).
    /// </summary>
    public required TKey Key { get; init; }

    /// <summary>
    /// Service provider for resolving dependencies.
    /// </summary>
    public required IServiceProvider ServiceProvider { get; init; }

    /// <summary>
    /// Cancellation token for the current fetch operation.
    /// </summary>
    public CancellationToken? CancellationToken { get; init; }
}