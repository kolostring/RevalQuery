namespace RevalQuery.Core;

public sealed class FetchOptionsBuilder
{
    private TimeSpan? _refetchInterval;
    private TimeSpan? _staleTime;
    private int? _retry;
    private Func<int, TimeSpan>? _retryDelay;

    public FetchOptionsBuilder(FetchOptions? existing = null)
    {
        if (existing == null) return;
        _refetchInterval = existing.RefetchInterval;
        _staleTime = existing.StaleTime;
        _retry = existing.Retry;
        _retryDelay = existing.RetryDelay;
    }

    public FetchOptionsBuilder RefetchInterval(TimeSpan interval)
    {
        _refetchInterval = interval;
        return this;
    }

    public FetchOptionsBuilder StaleTime(TimeSpan time)
    {
        _staleTime = time;
        return this;
    }

    public FetchOptionsBuilder Retry(int count, Func<int, TimeSpan>? delay = null)
    {
        _retry = count;
        if (delay != null) _retryDelay = delay;
        return this;
    }

    public FetchOptions Build() => new(
        RefetchInterval: _refetchInterval,
        StaleTime: _staleTime,
        Retry: _retry,
        RetryDelay: _retryDelay
    );

    public static implicit operator FetchOptions(FetchOptionsBuilder builder)
        => builder.Build();
}

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