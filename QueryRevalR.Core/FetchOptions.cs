namespace QueryRevalR;

public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null,
    int Retry = 3,
    Func<int, TimeSpan>? RetryDelay = null
);