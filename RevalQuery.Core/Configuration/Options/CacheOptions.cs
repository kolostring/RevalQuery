namespace RevalQuery.Core.Configuration.Options;

public sealed record CacheOptions(TimeSpan GcTime)
{
    public static CacheOptions Default => new(
        TimeSpan.FromMinutes(5)
    );
};