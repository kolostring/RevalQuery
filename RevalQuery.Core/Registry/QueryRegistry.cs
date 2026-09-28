using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Registry;

/// <summary>
/// The in-memory structure holding every live query state, organised hierarchically by
/// query key. Not synchronised: <see cref="QueryClient"/> owns the lock covering it.
/// </summary>
internal sealed class QueryRegistry
{
    /// <summary>
    /// Stands in for a null key segment, because Dictionary rejects null keys.
    /// </summary>
    private static readonly object NullSegment = new();

    public RegistryNode Root { get; private set; } = new();

    /// <summary>
    /// Returns the node for this key, creating the path to it when missing.
    /// </summary>
    public RegistryNode GetOrCreateNode(ITuple key)
    {
        var current = Root;

        for (var i = 0; i < key.Length; i++)
        {
            var segment = key[i] ?? NullSegment;

            if (!current.Children.TryGetValue(segment, out var child))
            {
                child = new RegistryNode();
                current.Children[segment] = child;
            }

            current = child;
        }

        return current;
    }

    /// <summary>
    /// Returns the node for this key, or null when the path does not exist.
    /// </summary>
    public RegistryNode? PeekNode(ITuple key)
    {
        var current = Root;

        for (var i = 0; i < key.Length; i++)
        {
            var segment = key[i] ?? NullSegment;
            if (!current.Children.TryGetValue(segment, out var child)) return null;
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Removes the node for this key and every ancestor left holding neither a state nor children.
    /// </summary>
    public void PruneNode(ITuple key) => PruneRecursive(Root, key, 0);

    /// <summary>
    /// Drops every node. Used when the client is disposed.
    /// </summary>
    public void Clear() => Root = new RegistryNode();

    /// <summary>
    /// Collects the states held by this node and everything beneath it.
    /// </summary>
    public static List<IQueryState> StatesFrom(RegistryNode node)
    {
        var result = new List<IQueryState>();
        CollectStates(node, result);
        return result;
    }

    /// <summary>
    /// Collects the workers held by this node and everything beneath it.
    /// </summary>
    public static List<IDisposable> WorkersFrom(RegistryNode node)
    {
        var result = new List<IDisposable>();
        CollectWorkers(node, result);
        return result;
    }

    private static bool PruneRecursive(RegistryNode current, ITuple key, int keyIndex)
    {
        if (keyIndex >= key.Length) return IsRemovable(current);

        var segment = key[keyIndex] ?? NullSegment;
        if (!current.Children.TryGetValue(segment, out var child)) return false;

        if (!PruneRecursive(child, key, keyIndex + 1)) return false;

        current.Children.Remove(segment);
        return IsRemovable(current);
    }

    private static bool IsRemovable(RegistryNode node) => node.State is null && node.Children.Count == 0;

    private static void CollectStates(RegistryNode node, List<IQueryState> result)
    {
        if (node.State is not null) result.Add(node.State);
        foreach (var child in node.Children.Values) CollectStates(child, result);
    }

    private static void CollectWorkers(RegistryNode node, List<IDisposable> result)
    {
        if (node.Worker is not null) result.Add(node.Worker);
        foreach (var child in node.Children.Values) CollectWorkers(child, result);
    }
}
