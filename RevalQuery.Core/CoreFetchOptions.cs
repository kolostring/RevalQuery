using System;

namespace RevalQuery.Core;

public record CoreFetchOptions(
    TimeSpan RefetchInterval,
    TimeSpan StaleTime,
    int Retry,
    Func<int, TimeSpan> RetryDelay
)
{
    public static CoreFetchOptions Default => new(
        RefetchInterval: TimeSpan.Zero,
        StaleTime: TimeSpan.Zero,
        Retry: 3,
        RetryDelay: attempt
            => TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt), 30000))
    );
};