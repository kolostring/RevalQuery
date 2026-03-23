namespace QueryRevalR.Core;

public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null,
    int? Retry = null,
    Func<int, TimeSpan>? RetryDelay = null
)
{
    public CoreFetchOptions PatchNullFields(CoreFetchOptions value)
    {
        return new(
            RefetchInterval: RefetchInterval ?? value.RefetchInterval,
            StaleTime: StaleTime ?? value.StaleTime,
            Retry: Retry ?? value.Retry,
            RetryDelay: RetryDelay ?? value.RetryDelay
        );
    }
};