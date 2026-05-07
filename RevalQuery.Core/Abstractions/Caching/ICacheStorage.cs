using System.Runtime.CompilerServices;
using RevalQuery.Core.Caching.Storage;

namespace RevalQuery.Core.Abstractions.Caching;

/// <summary>
/// Abstraction for cache storage implementations.
/// Supports different storage strategies.
/// </summary>
public interface ICacheStorage
{
    /// <summary>
    /// Gets or creates a cache node for the given key segments.
    /// Creates the hierarchical path if it doesn't exist.
    /// </summary>
    /// <param name="keySegments">The key as an ITuple (e.g., ("users", 1)).</param>
    void GetOrCreateNode(ITuple keySegments);

    /// <summary>
    /// Peeks at a cache node by its tuple key segments without modification.
    /// Returns null if not found.
    /// </summary>
    /// <param name="keySegments">The key as an ITuple.</param>
    CacheNode? PeekNode(ITuple keySegments);

    /// <summary>
    /// Peeks at a cache node by a single-segment string key.
    /// Returns null if not found.
    /// </summary>
    /// <param name="key">The string key.</param>
    CacheNode? PeekNode(string key);

    /// <summary>
    /// Prunes (removes) a node and its empty parent nodes from the cache tree.
    /// </summary>
    /// <param name="keySegments">The key to prune.</param>
    /// <returns>True if the node was found and pruned.</returns>
    bool PruneNode(ITuple keySegments);

    /// <summary>
    /// Retrieves all child nodes recursively from the given node.
    /// Used for prefix-based invalidation.
    /// </summary>
    /// <param name="node">The parent node to traverse.</param>
    /// <returns>Collection of all descendant nodes.</returns>
    ICollection<CacheNode> GetChildNodes(CacheNode node);

    /// <summary>
    /// Gets the root node of the cache storage.
    /// </summary>
    CacheNode RootNode { get; }
}