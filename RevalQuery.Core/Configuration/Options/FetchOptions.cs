namespace RevalQuery.Core.Configuration.Options;

public record CoreFetchOptions(
    TimeSpan RefetchInterval,
    TimeSpan StaleTime
)
{
    public static CoreFetchOptions Default => new(
        TimeSpan.Zero,
        TimeSpan.Zero
    );

    public CoreFetchOptions Apply(FetchOptions? options)
    {
        return options is null
            ? this
            : new CoreFetchOptions(
                options.RefetchInterval ?? RefetchInterval,
                options.StaleTime ?? StaleTime
            );
    }
};

public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null
)
{
    public static FetchOptionsBuilder Create()
    {
        return new FetchOptionsBuilder();
    }
}

public sealed class FetchOptionsBuilder
{
    private TimeSpan? _refetchInterval;
    private TimeSpan? _staleTime;

    public FetchOptionsBuilder(FetchOptions? existing = null)
    {
        if (existing == null) return;
        _refetchInterval = existing.RefetchInterval;
        _staleTime = existing.StaleTime;
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

    public FetchOptions Build()
    {
        return new FetchOptions(
            _refetchInterval,
            _staleTime
        );
    }

    public static implicit operator FetchOptions(FetchOptionsBuilder builder)
    {
        return builder.Build();
    }
}