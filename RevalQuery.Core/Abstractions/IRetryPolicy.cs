using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Abstractions;

/// <summary>
/// Strategy interface for retry logic.
/// Allows custom retry implementations (exponential backoff, fixed delay, etc.).
/// </summary>
public interface IRetryPolicy
{
    /// <summary>
    /// Executes the handler with retry logic.
    /// </summary>
    Task<TResponse> ExecuteWithRetryAsync<TResponse>(
        Func<Task<TResponse>> handler,
        CoreRetryOptions retryOptions,
        CancellationToken cancellationToken = default
    );
}