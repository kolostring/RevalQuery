using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Token tracking eviction state for a cached query entry.
/// Used by TtlQueryGarbageCollector.
/// </summary>
public sealed class EvictionToken
{
    /// <summary>
    /// The cache key.
    /// </summary>
    public ITuple Key { get; init; } = null!;

    /// <summary>
    /// When this entry expires (eligible for eviction).
    /// </summary>
    public DateTime Expiry { get; init; }

    /// <summary>
    /// Hash code of the key.
    /// </summary>
    public int KeyHashCode { get; init; }
}