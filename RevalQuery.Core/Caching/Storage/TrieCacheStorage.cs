using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Caching;

namespace RevalQuery.Core.Caching.Storage;

/// <summary>
/// Trie-based implementation of cache storage.
/// Organizes cache entries hierarchically by key segments for efficient prefix operations.
/// </summary>
public sealed class TrieCacheStorage : ICacheStorage
{
    private readonly CacheNode _root = new(0);

    /// <summary>
    /// The root node of the trie.
    /// </summary>
    public CacheNode RootNode => _root;

    /// <summary>
    /// Gets or creates a node for the given key segments.
    /// Creates the hierarchical path if it doesn't exist.
    /// </summary>
    public void GetOrCreateNode(ITuple keySegments)
    {
        var current = _root;
        var hashCode = new HashCode();

        for (var i = 0; i < keySegments.Length; i++)
        {
            var segment = keySegments[i]?.ToString() ?? "null";
            hashCode.Add(keySegments[i]);

            if (!current.Children.TryGetValue(segment, out var value))
            {
                value = new CacheNode(hashCode.ToHashCode());
                current.Children[segment] = value;
            }

            current = value;
        }
    }

    /// <summary>
    /// Peeks at a node by tuple key without modification.
    /// </summary>
    public CacheNode? PeekNode(ITuple keySegments)
    {
        var current = _root;
        for (var i = 0; i < keySegments.Length; i++)
        {
            var segment = keySegments[i]?.ToString() ?? "null";

            if (!current.Children.TryGetValue(segment, out var value)) return null;

            current = value;
        }

        return current;
    }

    /// <summary>
    /// Peeks at a node by single-segment string key.
    /// </summary>
    public CacheNode? PeekNode(string key)
    {
        return _root.Children.GetValueOrDefault(key);
    }

    /// <summary>
    /// Prunes a node and its empty ancestors from the trie.
    /// </summary>
    public bool PruneNode(ITuple keySegments)
    {
        return PruneRecursive(_root, keySegments, 0);
    }

    /// <summary>
    /// Gets all descendant nodes of a given node (recursive).
    /// </summary>
    public ICollection<CacheNode> GetChildNodes(CacheNode node)
    {
        var result = new List<CacheNode>();
        TraverseRecursive(node, result);
        return result;
    }

    private bool PruneRecursive(CacheNode current, ITuple key, int keyIndex)
    {
        if (keyIndex >= key.Length) return current.Children.Count == 0;

        var segment = key[keyIndex]?.ToString() ?? "null";

        if (!current.Children.TryGetValue(segment, out var child))
            return false;

        var shouldDeleteChild = PruneRecursive(child, key, keyIndex + 1);

        if (!shouldDeleteChild)
            return false;

        current.Children.Remove(segment);
        return current.Children.Count == 0;
    }

    private void TraverseRecursive(CacheNode node, List<CacheNode> result)
    {
        result.Add(node);

        foreach (var child in node.Children.Values) TraverseRecursive(child, result);
    }
}