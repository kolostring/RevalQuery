using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Persistence;

namespace RevalQuery.Core.Persistence;

internal sealed class CompositeQueryPersistence(IReadOnlyList<IQueryPersistence> stores) : IQueryPersistence
{
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
            }
        }
    }
}
