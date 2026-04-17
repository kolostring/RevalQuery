using RevalQuery.Core.Configuration.Options;

namespace RevalQuery.Core.Query.Options;

public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null,
    int? Retry = null,
    Func<int, TimeSpan>? RetryDelay = null
)
{
    public static FetchOptionsBuilder Create() => new();
    
    public CoreFetchOptions PatchNullFields(CoreFetchOptions defaults)
    {
        return new CoreFetchOptions(
            RefetchInterval ?? defaults.RefetchInterval,
            StaleTime ?? defaults.StaleTime,
            Retry ?? defaults.Retry,
            RetryDelay ?? defaults.RetryDelay
        );
    }
}

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