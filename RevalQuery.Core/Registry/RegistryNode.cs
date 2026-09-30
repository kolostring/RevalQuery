using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Registry;

/// <summary>
/// One node of the registry trie. A node holds the query state for the key path that
/// reaches it, the worker driving that state, and the child segments below it.
/// </summary>
internal sealed class RegistryNode
{
    /// <summary>
    /// Child nodes keyed by the boxed key segment itself.
    /// </summary>
    public Dictionary<object, RegistryNode> Children { get; } = new();

    /// <summary>
    /// The query state at this key, or null when this node only exists as a path to children.
    /// </summary>
    public IQueryState? State { get; set; }

    /// <summary>
    /// The worker driving this node's query, or null when no fetch is being managed.
    /// </summary>
    public IQueryWorker? Worker { get; set; }
}
