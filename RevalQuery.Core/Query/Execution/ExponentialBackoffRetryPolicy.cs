using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Query.Execution;

/// <summary>
/// Default retry policy with exponential backoff strategy.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IQueryRetryPolicy
{
    public async Task<TResponse> ExecuteWithRetryAsync<TKey, TResponse>(
        Func<Task<TResponse>> handler,
        int maxAttempts,
        Func<int, TimeSpan> retryDelayCalculator,
        CancellationToken cancellationToken = default
    ) where TKey : ITuple
    {
        for (var attempt = 0; attempt <= maxAttempts; attempt++)
            try
            {
                if (attempt <= 0) return await handler();
                var delay = retryDelayCalculator(attempt - 1);
                await Task.Delay(delay, cancellationToken);

                return await handler();
            }
            catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Continue to next attempt
            }

        // Should never reach here, but for completeness
        return await handler();
    }
}