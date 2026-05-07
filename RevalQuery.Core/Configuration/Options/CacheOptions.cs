namespace RevalQuery.Core.Configuration.Options;

/// <summary>
/// Internal immutable cache options with applied defaults.
/// </summary>
public sealed record CoreCacheOptions(TimeSpan GcTime, TimeSpan GcInterval)
{
    /// <summary>
    /// Default: 5 minute TTL, 1 minute GC interval.
    /// </summary>
    public static CoreCacheOptions Default => new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1)
    );

    /// <summary>
    /// Applies user overrides to these defaults.
    /// </summary>
    public CoreCacheOptions Apply(CacheOptions? options)
    {
        return options is null
            ? this
            : this with { GcTime = options.GcTime ?? GcTime };
    }
};

/// <summary>
/// User-facing cache options - nullable GcTime for override.
/// </summary>
public sealed record CacheOptions(TimeSpan? GcTime = null);

/// <summary>
/// Fluent builder for CacheOptions.
/// </summary>
public sealed class CacheOptionsBuilder
{
    private TimeSpan? _gcTime;

    public CacheOptionsBuilder(CacheOptions? existing = null)
    {
        if (existing == null) return;
        _gcTime = existing.GcTime;
    }

    /// <summary>
    /// Sets the time-to-live for cached entries (when to evict).
    /// </summary>
    public CacheOptionsBuilder GcTime(TimeSpan? gcTime)
    {
        _gcTime = gcTime;
        return this;
    }

    /// <summary>
    /// Builds the CacheOptions.
    /// </summary>
    public CacheOptions Build()
    {
        return new CacheOptions(
            _gcTime
        );
    }

    /// <summary>
    /// Implicit conversion to CacheOptions.
    /// </summary>
    public static implicit operator CacheOptions(CacheOptionsBuilder builder)
    {
        return builder.Build();
    }
}