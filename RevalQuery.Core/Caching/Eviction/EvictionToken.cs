using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Token used to track and manage eviction state for cached queries.
/// </summary>
public sealed class EvictionToken
{
    public ITuple Key { get; init; } = null!;
    public DateTime Expiry { get; init; }
    public int KeyHashCode { get; init; }
}