using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Caching.Key;

/// <summary>
/// Utility for calculating hash codes for cache keys.
/// </summary>
public static class CacheKeyCalculator
{
    /// <summary>
    /// Calculates a HashCode for the given tuple key.
    /// </summary>
    public static HashCode CalculateHash(ITuple key)
    {
        var hash = new HashCode();
        for (var i = 0; i < key.Length; i++) hash.Add(key[i]);
        return hash;
    }

    /// <summary>
    /// Gets the hash code value as an integer.
    /// </summary>
    public static int GetHashCode(ITuple key)
    {
        return CalculateHash(key).ToHashCode();
    }
}