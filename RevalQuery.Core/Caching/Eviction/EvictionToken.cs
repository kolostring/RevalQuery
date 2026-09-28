using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Caching.Eviction;

/// <summary>
/// Tracks one unobserved query waiting to be evicted.
/// Used by <see cref="TtlQueryGarbageCollector"/>.
/// </summary>
public sealed class EvictionToken
{
    /// <summary>
    /// The query key.
    /// </summary>
    public ITuple Key { get; init; } = null!;

    /// <summary>
    /// When the query becomes eligible for eviction.
    /// </summary>
    public DateTime Expiry { get; init; }
}
