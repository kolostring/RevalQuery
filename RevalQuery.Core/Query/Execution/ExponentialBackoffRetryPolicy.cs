using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Query.Execution;

/// <summary>
/// Default retry policy with exponential backoff strategy.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    public async Task<TResponse> ExecuteWithRetryAsync<TResponse>(
        Func<Task<TResponse>> handler,
        CoreRetryOptions retryOptions,
        CancellationToken cancellationToken = default
    )
    {
        var maxAttempts = retryOptions.Retry;
        var retryDelayCalculator = retryOptions.RetryDelay;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    var delay = retryDelayCalculator(attempt - 1);
                    await Task.Delay(delay, cancellationToken);
                }

                return await handler();
            }
            catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Continue to next attempt
            }
        }

        throw new InvalidOperationException("Retry policy failed to return result.");
    }
}