using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Abstractions.Persistence;

/// <summary>
/// A durable copy of query data outside the registry, surviving process restarts.
/// Supplied by the consuming application: register an implementation and the client
/// loads a query's data when the query is created and saves it after every successful fetch.
/// </summary>
/// <remarks>
/// Adapters own serialisation and key encoding. The library never serialises, which keeps
/// System.Text.Json and its trimming constraints out of the core and out of WebAssembly payloads.
/// </remarks>
public interface IQueryPersistence
{
    /// <summary>
    /// Loads the persisted entry for a key, or null when nothing is stored.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default);

    /// <summary>
    /// Saves the entry for a key, replacing anything already stored.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="entry">The data and the timestamp it was fetched at.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default);
}

/// <summary>
/// One persisted query: its data and the moment that data was fetched.
/// </summary>
/// <typeparam name="TRes">The query's result type.</typeparam>
/// <param name="Data">The stored data.</param>
/// <param name="LastUpdatedAt">
/// When the data was fetched. Restored verbatim, so data that was already stale when the
/// process stopped is still stale on the next start and refetches immediately.
/// </param>
public sealed record PersistedQuery<TRes>(TRes Data, DateTimeOffset LastUpdatedAt);
