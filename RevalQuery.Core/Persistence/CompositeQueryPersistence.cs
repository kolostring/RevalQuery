using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Persistence;

namespace RevalQuery.Core.Persistence;

/// <summary>
/// Presents several persistence stores to the client as one.
/// </summary>
/// <remarks>
/// Lets a prerender transfer and a durable store of the consumer's own coexist rather than one
/// replacing the other. A load takes the first store that has the key, so registration order is
/// preference order; a save goes to every store. One store failing does not stop the others,
/// because a store is a third party and reporting its own failures is its own job.
/// </remarks>
internal sealed class CompositeQueryPersistence(IReadOnlyList<IQueryPersistence> stores) : IQueryPersistence
{
    /// <summary>
    /// Returns the single store, one wrapping all of them, or null when there are none.
    /// </summary>
    public static IQueryPersistence? From(IEnumerable<IQueryPersistence> stores)
    {
        var list = stores as IReadOnlyList<IQueryPersistence> ?? [.. stores];

        return list.Count switch
        {
            0 => null,
            1 => list[0],
            _ => new CompositeQueryPersistence(list)
        };
    }

    public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
    {
        foreach (var store in stores)
        {
            try
            {
                if (await store.LoadAsync<TRes>(key, ct) is { } entry) return entry;
            }
            catch
            {
                // Try the next store rather than leaving the query with nothing
            }
        }

        return null;
    }

    public async ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
    {
        foreach (var store in stores)
        {
            try
            {
                await store.SaveAsync(key, entry, ct);
            }
            catch
            {
                // One store refusing the write must not cost the others theirs
            }
        }
    }
}
