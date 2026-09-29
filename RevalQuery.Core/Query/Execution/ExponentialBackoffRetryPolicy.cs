using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Query.Execution;

/// <summary>
/// Default retry policy with exponential backoff.
/// Retries on failure with increasing delays between attempts.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    /// <summary>
    /// Executes the handler, retrying a failure up to retryOptions.Retry further times with
    /// the delay from retryOptions.RetryDelay between them.
    /// </summary>
    /// <remarks>
    /// The retry count never includes the first attempt, so zero retries still calls the
    /// handler once and surfaces its exception.
    /// </remarks>
    public async Task<TResponse> ExecuteWithRetryAsync<TResponse>(
        Func<Task<TResponse>> handler,
        CoreRetryOptions retryOptions,
        CancellationToken cancellationToken = default
    )
    {
        var maxRetries = retryOptions.Retry;
        var retryDelayCalculator = retryOptions.RetryDelay;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = retryDelayCalculator(attempt);
                    await Task.Delay(delay, cancellationToken);
                }

                return await handler();
            }
            catch (Exception ex) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        // Unreachable: the final attempt either returns or throws past the filter above. The
        // rethrow is here so that if it ever is reached, the caller gets the real failure
        // rather than an invented one that discards it.
        throw lastException ?? new InvalidOperationException("Retry policy failed to return result.");
    }
}
