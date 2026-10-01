namespace RevalQuery.Core.Configuration.Options;

/// <summary>
/// Internal immutable fetch options with applied defaults.
/// </summary>
public record CoreFetchOptions(
    TimeSpan RefetchInterval,
    TimeSpan StaleTime,
    bool Static = false
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
                options.StaleTime ?? StaleTime,
                options.Static ?? Static
            );
    }
}

/// <summary>
/// User-facing fetch options - nullable fields for overrides.
/// </summary>
public sealed record FetchOptions(
    TimeSpan? RefetchInterval = null,
    TimeSpan? StaleTime = null,
    bool? Static = null
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
    private bool? _static;

    /// <summary>
    /// Creates a builder, optionally starting from options already set.
    /// </summary>
    /// <param name="existing">Options to start from, or null to start empty.</param>
    public FetchOptionsBuilder(FetchOptions? existing = null)
    {
        if (existing == null) return;
        _refetchInterval = existing.RefetchInterval;
        _staleTime = existing.StaleTime;
        _static = existing.Static;
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
    /// Declares the data never to go stale, so it is served from cache however old it is.
    /// </summary>
    /// <remarks>
    /// Not a very long <see cref="StaleTime"/>. A static query is also left alone by
    /// invalidation, which no duration achieves: the staleness decision asks whether the query
    /// is static before it asks whether it was invalidated. The query still fetches once, when
    /// it has no data, and a polling interval still drives it if one is set.
    /// </remarks>
    public FetchOptionsBuilder NeverStale()
    {
        _static = true;
        return this;
    }

    /// <summary>
    /// Builds the FetchOptions.
    /// </summary>
    public FetchOptions Build()
    {
        return new FetchOptions(
            _refetchInterval,
            _staleTime,
            _static
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