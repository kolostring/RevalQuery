using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Abstractions.Persistence;

/// <summary>
/// A durable copy of query data outside the registry, surviving process restarts.
/// Supplied by the consuming application: register an implementation and the client
/// loads a query's data when the query is created and saves it after every successful fetch.
/// </summary>
/// <remarks>
/// <para>Adapters own serialisation and key encoding. The library never serialises, which keeps
/// System.Text.Json and its trimming constraints out of the core and out of WebAssembly
/// payloads.</para>
/// <para>Every method must complete or fault, and must never hang. The library awaits a load
/// with no timeout, by decision, so an adapter that never returns stalls its query for the life
/// of the process. Apply your own timeout inside the adapter and fault instead: a faulted load
/// leaves the query to fetch as though nothing was stored, which is a recoverable outcome, and a
/// hung one is not.</para>
/// </remarks>
public interface IQueryPersistence
{
    /// <summary>
    /// Loads the persisted entry for a key, or null when nothing is stored.
    /// </summary>
    /// <remarks>
    /// Must complete or fault, never hang. Faulting is treated as nothing being stored. The
    /// query reports itself loading until this returns, so a load that never finishes leaves
    /// its query loading forever.
    /// </remarks>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default);

    /// <summary>
    /// Saves the entry for a key, replacing anything already stored.
    /// </summary>
    /// <remarks>
    /// Must complete or fault, never hang. A fetch awaits this before it is finished, so a save
    /// that never returns holds the fetch open. Faulting is ignored: the data is already in the
    /// registry and usable, and reporting the storage failure is the adapter's own job.
    /// </remarks>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="entry">The data and the timestamp it was fetched at.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default);
}

/// <summary>
/// One persisted query: its data, and what the query knew about that data's age.
/// </summary>
/// <typeparam name="TRes">The query's result type.</typeparam>
/// <param name="Data">The stored data.</param>
/// <param name="Freshness">
/// Restored verbatim, so data that was already stale when the process stopped is still stale on
/// the next start and refetches immediately, and an invalidation that was never acted on is
/// still outstanding rather than forgotten.
/// </param>
public sealed record PersistedQuery<TRes>(TRes Data, QueryFreshness Freshness);
