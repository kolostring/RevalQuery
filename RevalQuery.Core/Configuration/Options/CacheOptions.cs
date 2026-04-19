namespace RevalQuery.Core.Configuration.Options;

public sealed record CoreCacheOptions(TimeSpan GcTime, TimeSpan GcInterval)
{
    public static CoreCacheOptions Default => new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1)
    );

    public CoreCacheOptions Apply(CacheOptions? options)
    {
        return options is null
            ? this
            : this with { GcTime = options.GcTime ?? GcTime };
    }
};

public sealed record CacheOptions(TimeSpan? GcTime = null);

public sealed class CacheOptionsBuilder
{
    private TimeSpan? _gcTime;

    public CacheOptionsBuilder(CacheOptions? existing = null)
    {
        if (existing == null) return;
        _gcTime = existing.GcTime;
    }

    public CacheOptionsBuilder GcTime(TimeSpan? gcTime)
    {
        _gcTime = gcTime;
        return this;
    }

    public CacheOptions Build()
    {
        return new CacheOptions(
            _gcTime
        );
    }

    public static implicit operator CacheOptions(CacheOptionsBuilder builder)
    {
        return builder.Build();
    }
}