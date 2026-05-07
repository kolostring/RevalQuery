namespace RevalQuery.Core.Configuration.Options;

/// <summary>
/// Internal immutable fetch options with applied defaults.
/// </summary>
public record CoreFetchOptions(
    TimeSpan RefetchInterval,
    TimeSpan StaleTime
)
{
    /// <summary>
    /// Default: Zero intervals (no polling, always stale).
    /// </summary>
    public static CoreFetchOptions Default => new(
        TimeSpan.Zero,
        TimeSpan.Zero
    );

    /// <summary>
    /// Applies user overrides to these defaults.
    /// </summary>
    public CoreFetchOptions Apply(FetchOptions? options)
    {
        return options is null
            ? this
            : new CoreFetchOptions(
                options.RefetchInterval ?? RefetchInterval,
                options.StaleTime ?? StaleTime
            );
    }
}

/// <summary>
/// User-facing fetch options - nullable fields for overrides.
/// </summary>
public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null
)
{
    /// <summary>
    /// Creates a new FetchOptionsBuilder.
    /// </summary>
    public static FetchOptionsBuilder Create()
    {
        return new FetchOptionsBuilder();
    }
}

/// <summary>
/// Fluent builder for FetchOptions.
/// </summary>
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

    /// <summary>
    /// Sets the automatic refetch interval (polling).
    /// </summary>
    public FetchOptionsBuilder RefetchInterval(TimeSpan interval)
    {
        _refetchInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets how long data is considered fresh before refetching.
    /// </summary>
    public FetchOptionsBuilder StaleTime(TimeSpan time)
    {
        _staleTime = time;
        return this;
    }

    /// <summary>
    /// Builds the FetchOptions.
    /// </summary>
    public FetchOptions Build()
    {
        return new FetchOptions(
            _refetchInterval,
            _staleTime
        );
    }

    /// <summary>
    /// Implicit conversion to FetchOptions.
    /// </summary>
    public static implicit operator FetchOptions(FetchOptionsBuilder builder)
    {
        return builder.Build();
    }
}