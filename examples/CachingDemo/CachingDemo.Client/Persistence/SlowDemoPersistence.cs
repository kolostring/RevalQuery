using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;

namespace CachingDemo.Client.Persistence;

/// <summary>
/// A deliberately slow in-memory store, so a restore is long enough to watch.
/// </summary>
/// <remarks>
/// <para>Only answers for keys whose first segment is "persisted". Every other key returns
/// null immediately, which keeps the rest of the demo unaffected by a store that exists to
/// make one page interesting.</para>
/// <para>A real adapter would read the browser's local storage or an IndexedDB database here.
/// What matters for the demo is the delay: a query with a restore outstanding reports
/// IsRestoring, not IsFetching, and IsLoading covers both.</para>
/// </remarks>
public sealed class SlowDemoPersistence : IQueryPersistence
{
    /// <summary>How long a load takes, whether or not it finds anything.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, object> Store = new();

    /// <summary>
    /// Puts an entry in the store before any query asks for it, standing in for data a
    /// previous run of the app left behind.
    /// </summary>
    public static void Seed<TRes>(ITuple key, TRes data, DateTimeOffset lastUpdatedAt) =>
        Store[Encode(key)] = new PersistedQuery<TRes>(data, new QueryFreshness(lastUpdatedAt));

    /// <inheritdoc />
    public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
    {
        if (!Owns(key)) return null;

        await Task.Delay(Delay, ct);

        return Store.TryGetValue(Encode(key), out var stored) ? stored as PersistedQuery<TRes> : null;
    }

    /// <inheritdoc />
    public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
    {
        if (Owns(key)) Store[Encode(key)] = entry;
        return ValueTask.CompletedTask;
    }

    // An adapter owns its own key encoding. This one keeps it to the shape the library
    // guarantees: a key is a tuple of segments, compared by value and never hashed.
    private static string Encode(ITuple key) =>
        string.Join('/', Enumerable.Range(0, key.Length).Select(i => key[i]?.ToString() ?? ""));

    private static bool Owns(ITuple key) => key.Length > 0 && key[0] as string == "persisted";
}
