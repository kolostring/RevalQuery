using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Tests;

/// <summary>
/// Reads a client's registry under the client's own lock, so a walk never races
/// the library mutating the tree.
/// </summary>
internal static class RegistryProbe
{
    /// <summary>
    /// How many workers the client's registry holds.
    /// </summary>
    public static int WorkerCount(RevalClient client) =>
        Locked(client, registry => QueryRegistry.WorkersFrom(registry.Root).Count);

    /// <summary>
    /// Fails if any registry node holds a worker but no state.
    /// </summary>
    public static void AssertEveryWorkerHasItsState(RevalClient client) =>
        Locked(client, registry =>
        {
            Walk(registry.Root);
            return 0;
        });

    private static T Locked<T>(RevalClient client, Func<QueryRegistry, T> read) =>
        client.ReadRegistry(read);

    private static void Walk(RegistryNode node)
    {
        Assert.False(
            node.Worker is not null && node.State is null,
            "a registry node holds a worker but no state, so the next subscriber to that " +
            "key would get a state nothing is driving");

        foreach (var child in node.Children.Values) Walk(child);
    }
}
