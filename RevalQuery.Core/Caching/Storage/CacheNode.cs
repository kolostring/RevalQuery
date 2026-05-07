namespace RevalQuery.Core.Caching.Storage;

/// <summary>
/// Represents a node in the cache trie structure.
/// Each node corresponds to a key segment; leaves hold query state.
/// </summary>
public sealed class CacheNode(int keyHashCode)
{
    /// <summary>
    /// Hash code computed from the full key path to this node.
    /// </summary>
    public int KeyHashCode { get; init; } = keyHashCode;

    /// <summary>
    /// Child nodes keyed by segment string.
    /// </summary>
    public Dictionary<string, CacheNode> Children { get; set; } = new();
}