namespace RevalQuery.Core.Caching.Storage;

/// <summary>
/// Represents a node in the cache trie structure.
/// </summary>
public sealed class CacheNode(int keyHashCode)
{
    public int KeyHashCode { get; init; } = keyHashCode;
    public Dictionary<string, CacheNode> Children { get; set; } = new();
}