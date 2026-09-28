using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Registry;

/// <summary>
/// Compares query keys segment by segment using each segment's own value equality.
/// Segments are never hashed to a lookup key nor converted to text, so <c>("users", 1)</c>
/// and <c>("users", "1")</c> stay distinct and formatting stays culture independent.
/// </summary>
public sealed class QueryKeyComparer : IEqualityComparer<ITuple>
{
    /// <summary>
    /// The shared instance. The comparer is stateless.
    /// </summary>
    public static QueryKeyComparer Instance { get; } = new();

    private QueryKeyComparer()
    {
    }

    /// <summary>
    /// Two keys are equal when they have the same length and every segment is equal.
    /// </summary>
    public bool Equals(ITuple? x, ITuple? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;

        for (var i = 0; i < x.Length; i++)
            if (!object.Equals(x[i], y[i]))
                return false;

        return true;
    }

    /// <summary>
    /// Combines the segment hashes. Collisions are resolved by <see cref="Equals(ITuple?, ITuple?)"/>,
    /// so this value never decides identity on its own.
    /// </summary>
    public int GetHashCode(ITuple obj)
    {
        var hash = new HashCode();
        hash.Add(obj.Length);
        for (var i = 0; i < obj.Length; i++) hash.Add(obj[i]);
        return hash.ToHashCode();
    }
}
