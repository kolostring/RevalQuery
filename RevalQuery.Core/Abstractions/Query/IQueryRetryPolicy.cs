using System.Runtime.CompilerServices;
using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Strategy interface for query retry logic.
/// Allows custom retry implementations (exponential backoff, fixed delay, etc.).
/// </summary>
public interface IQueryRetryPolicy
{
    /// <summary>
    /// Executes the handler with retry logic.
    /// </summary>
    Task<TResponse> ExecuteWithRetryAsync<TKey, TResponse>(
        Func<Task<TResponse>> handler,
        CoreRetryOptions retryOptions,
        CancellationToken cancellationToken = default
    ) where TKey : ITuple;
}