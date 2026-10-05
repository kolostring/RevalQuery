using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Registry;

internal sealed class QueryRegistry
{
    private static readonly object NullSegment = new();

    public RegistryNode Root { get; private set; } = new();

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

    public void PruneNode(ITuple key) => PruneRecursive(Root, key, 0);

    public void Clear() => Root = new RegistryNode();

    public static List<IQueryState> StatesFrom(RegistryNode node)
    {
        var result = new List<IQueryState>();
        CollectStates(node, result);
        return result;
    }

    public static List<IQueryWorker> WorkersFrom(RegistryNode node)
    {
        var result = new List<IQueryWorker>();
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

    private static bool IsRemovable(RegistryNode node) =>
        node.State is null && node.Worker is null && node.Children.Count == 0;

    private static void CollectStates(RegistryNode node, List<IQueryState> result)
    {
        if (node.State is not null) result.Add(node.State);
        foreach (var child in node.Children.Values) CollectStates(child, result);
    }

    private static void CollectWorkers(RegistryNode node, List<IQueryWorker> result)
    {
        if (node.Worker is not null) result.Add(node.Worker);
        foreach (var child in node.Children.Values) CollectWorkers(child, result);
    }
}
