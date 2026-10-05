using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;

namespace CachingDemo.Client.Persistence;

public sealed class SlowDemoPersistence : IQueryPersistence
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, object> Store = new();

    public static void Seed<TRes>(ITuple key, TRes data, DateTimeOffset lastUpdatedAt) =>
        Store[Encode(key)] = new PersistedQuery<TRes>(data, new QueryFreshness(lastUpdatedAt));

    public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
    {
        if (!Owns(key)) return null;

        await Task.Delay(Delay, ct);

        return Store.TryGetValue(Encode(key), out var stored) ? stored as PersistedQuery<TRes> : null;
    }

    public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
    {
        if (Owns(key)) Store[Encode(key)] = entry;
        return ValueTask.CompletedTask;
    }

    private static string Encode(ITuple key) =>
        string.Join('/', Enumerable.Range(0, key.Length).Select(i => key[i]?.ToString() ?? ""));

    private static bool Owns(ITuple key) => key.Length > 0 && key[0] as string == "persisted";
}
