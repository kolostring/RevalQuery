using System;

namespace RevalQuery.Core;

public sealed record CacheOptions(TimeSpan GcTime)
{
    public static CacheOptions Default => new(
        GcTime: TimeSpan.FromMinutes(5)
    );
};