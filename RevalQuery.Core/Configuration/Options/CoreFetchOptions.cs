namespace RevalQuery.Core.Configuration.Options;

public record CoreFetchOptions(
    TimeSpan RefetchInterval,
    TimeSpan StaleTime,
    int Retry,
    Func<int, TimeSpan> RetryDelay
)
{
    public static CoreFetchOptions Default => new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        3,
        attempt
            => TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt), 30000))
    );
};