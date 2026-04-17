using System.Runtime.CompilerServices;
using RevalQuery.Core.Caching.Storage;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// Abstraction for cache storage implementations.
/// Supports different storage strategies (Trie, Redis, In-Memory, etc.).
/// </summary>
public interface ICacheStorage
{
    /// <summary>
    /// Gets or creates a cache node for the given key segments.
    /// </summary>
    void GetOrCreateNode(ITuple keySegments);

    /// <summary>
    /// Peeks at a cache node by its tuple key segments without modification.
    /// </summary>
    CacheNode? PeekNode(ITuple keySegments);

    /// <summary>
    /// Peeks at a cache node by a string key.
    /// </summary>
    CacheNode? PeekNode(string key);

    /// <summary>
    /// Prunes (removes) a node and its empty parents from the cache tree.
    /// </summary>
    bool PruneNode(ITuple keySegments);

    /// <summary>
    /// Retrieves all child nodes recursively from the given node.
    /// </summary>
    ICollection<CacheNode> GetChildNodes(CacheNode node);

    /// <summary>
    /// Gets the root node of the cache storage.
    /// </summary>
    CacheNode RootNode { get; }
}