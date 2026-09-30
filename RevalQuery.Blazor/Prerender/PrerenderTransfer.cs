using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;

namespace RevalQuery.Blazor.Prerender;

/// <summary>
/// Carries the queries a prerender resolved across to the interactive client, so the client
/// renders from data the server already fetched instead of fetching the same keys again.
/// </summary>
/// <remarks>
/// <para>Both halves live in this one class. On the server it registers a persisting callback
/// that dehydrates every resolved query; on the client it reads what arrived and hands each
/// query its data as the query is created. Neither half needs to know which side it is on: a
/// server has nothing to take, and a client has nothing to dehydrate.</para>
/// <para>It reaches the client through <see cref="IQueryPersistence"/>, which already restores
/// data at the one moment a query enters the registry and already keeps the original fetch time
/// so restored data is correctly stale. Register a store of your own as well and both are used.</para>
/// </remarks>
public sealed class PrerenderTransfer : IQueryPersistence, IDisposable
{
    /// <summary>
    /// The key the whole payload is persisted under. One entry, not one per query, because the
    /// framework reads it back on a trimmed runtime and only a string is safe to ask it for.
    /// </summary>
    internal const string StateKey = "RevalQuery.PrerenderTransfer";

    private readonly PersistentComponentState _state;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly PersistingComponentStateSubscription _subscription;
    private readonly object _gate = new();

    private Dictionary<string, TransferEntry>? _arrived;
    private bool _isDisposed;

    /// <summary>
    /// Creates the transfer and subscribes it to the prerender's persisting phase.
    /// </summary>
    /// <param name="state">The framework's store for state crossing the prerender boundary.</param>
    /// <param name="serviceProvider">
    /// Resolves the <see cref="QueryClient"/> at persist time rather than now, because the client
    /// depends on this transfer and cannot be asked for while it is still being constructed.
    /// </param>
    /// <param name="serializerOptions">
    /// The consumer's options. Its TypeInfoResolver is what serialises query data, since the
    /// framework offers no way to hand one to PersistAsJson.
    /// </param>
    public PrerenderTransfer(
        PersistentComponentState state,
        IServiceProvider serviceProvider,
        JsonSerializerOptions serializerOptions)
    {
        _state = state;
        _serviceProvider = serviceProvider;
        _serializerOptions = serializerOptions;
        _subscription = state.RegisterOnPersisting(PersistAsync);
    }

    /// <summary>
    /// Returns the data this key arrived with, or null when it did not arrive.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
    {
        var arrived = Arrived();

        if (arrived.Count == 0) return new ValueTask<PersistedQuery<TRes>?>((PersistedQuery<TRes>?)null);

        var encoded = QueryKeyEncoder.TryEncode(key);

        if (encoded is null || !arrived.TryGetValue(encoded, out var entry))
        {
            return new ValueTask<PersistedQuery<TRes>?>((PersistedQuery<TRes>?)null);
        }

        // A key reused for a different result type must produce nothing rather than an object
        // of the wrong shape that happened to deserialise.
        if (entry.Type != typeof(TRes).ToString())
        {
            return new ValueTask<PersistedQuery<TRes>?>((PersistedQuery<TRes>?)null);
        }

        // TryGetTypeInfo, because GetTypeInfo throws NotSupportedException for a type the
        // resolver does not cover rather than returning null. A type the consumer left out of
        // its context is a query the client fetches for itself, not a failure.
        if (!_serializerOptions.TryGetTypeInfo(typeof(TRes), out var resolved) ||
            resolved is not JsonTypeInfo<TRes> typeInfo)
        {
            return new ValueTask<PersistedQuery<TRes>?>((PersistedQuery<TRes>?)null);
        }

        var data = JsonSerializer.Deserialize(entry.Json, typeInfo);

        return new ValueTask<PersistedQuery<TRes>?>(
            data is null ? null : new PersistedQuery<TRes>(data, entry.LastUpdatedAt));
    }

    /// <summary>
    /// Does nothing. The transfer dehydrates the whole registry once, when the prerender ends,
    /// rather than accumulating one fetch at a time.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="entry">The data and the timestamp it was fetched at.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
        default;

    /// <summary>
    /// Unsubscribes from the persisting phase. Idempotent.
    /// </summary>
    /// <remarks>
    /// One instance is registered under two service types, so the container captures it for
    /// disposal twice and calls this twice at the end of every scope. Whether unsubscribing
    /// twice is harmless is the framework's business, not something to depend on.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        _subscription.Dispose();
    }

    /// <summary>
    /// Dehydrates every resolved query into the framework's store.
    /// </summary>
    private Task PersistAsync()
    {
        var client = _serviceProvider.GetService<QueryClient>();

        if (client is null) return Task.CompletedTask;

        var queries = new Dictionary<string, TransferEntry>();

        foreach (var snapshot in client.SnapshotResolvedQueries())
        {
            var encoded = QueryKeyEncoder.TryEncode(snapshot.Key);

            // A key the encoder cannot address, or a type the consumer's resolver does not
            // know, is left for the client to fetch. Losing an optimisation is the right cost;
            // throwing here would fail the whole render.
            // TryGetTypeInfo, because GetTypeInfo throws NotSupportedException for an
            // uncovered type rather than returning null, and this runs inside the prerender's
            // persisting callback where throwing is the whole-render failure described above.
            if (encoded is null) continue;
            if (!_serializerOptions.TryGetTypeInfo(snapshot.DataType, out var typeInfo)) continue;

            queries[encoded] = new TransferEntry(
                snapshot.DataType.ToString(),
                JsonSerializer.Serialize(snapshot.Data, typeInfo),
                snapshot.LastUpdatedAt);
        }

        if (queries.Count == 0) return Task.CompletedTask;

        var payload = JsonSerializer.Serialize(
            new TransferPayload(queries), TransferSerializerContext.Default.TransferPayload);

        _state.PersistAsJson(StateKey, payload);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads what the prerender sent, once. Queries are created over the life of the client, so
    /// the payload is taken on the first lookup and kept, not taken per lookup.
    /// </summary>
    private Dictionary<string, TransferEntry> Arrived()
    {
        lock (_gate)
        {
            if (_arrived is not null) return _arrived;

            _arrived = Take() ?? [];

            return _arrived;
        }
    }

    private Dictionary<string, TransferEntry>? Take()
    {
        // string, not the payload type: the framework deserialises this one itself, and on a
        // trimmed runtime anything needing reflected metadata comes back empty. See ADR 0004.
        if (!_state.TryTakeFromJson<string>(StateKey, out var payload) || payload is null) return null;

        try
        {
            return JsonSerializer
                .Deserialize(payload, TransferSerializerContext.Default.TransferPayload)?.Queries;
        }
        catch (JsonException)
        {
            // A payload this version cannot read leaves every query to fetch normally
            return null;
        }
    }
}
