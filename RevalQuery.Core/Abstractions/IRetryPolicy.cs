using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Abstractions;

/// <summary>
/// Strategy interface for retry logic.
/// Allows custom retry implementations (exponential backoff, fixed delay, etc.).
/// </summary>
public interface IRetryPolicy
{
    /// <summary>
    /// Executes the handler with retry logic applied.
    /// </summary>
    /// <typeparam name="TResponse">The return type of the handler.</typeparam>
    /// <param name="handler">The async operation to execute.</param>
    /// <param name="retryOptions">Configuration for retry behavior (attempts, delay).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The result from successful handler execution.</returns>
    Task<TResponse> ExecuteWithRetryAsync<TResponse>(
        Func<Task<TResponse>> handler,
        CoreRetryOptions retryOptions,
        CancellationToken cancellationToken = default
    );
}