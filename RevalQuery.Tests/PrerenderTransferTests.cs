using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Blazor;
using RevalQuery.Blazor.Prerender;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public sealed record Widget(int Id, string Name);

[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(string))]
internal sealed partial class WidgetContext : JsonSerializerContext;

/// <summary>
/// Covers moving a prerender's resolved queries to the interactive client: the server
/// dehydrates what it fetched, the client renders from it instead of fetching the same keys
/// again, and data that was already stale stays stale.
/// </summary>
public class PrerenderTransferTests
{
    private static readonly JsonSerializerOptions Serializer =
        new() { TypeInfoResolver = WidgetContext.Default };

    private static int _serverCalls;
    private static int _clientCalls;

    private static QueryOptions<ValueTuple<string>, Widget> ServerQuery(string key) =>
        QueryOptions.Create(key, static _ =>
        {
            Interlocked.Increment(ref _serverCalls);
            return Task.FromResult(new Widget(7, "from server"));
        }).Build();

    private static QueryOptions<ValueTuple<string>, Widget> ClientQuery(string key, TimeSpan? staleTime = null)
    {
        var options = QueryOptions.Create(key, static _ =>
        {
            Interlocked.Increment(ref _clientCalls);
            return Task.FromResult(new Widget(9, "from client"));
        });

        if (staleTime is not null) options.ConfigureFetch(f => f.StaleTime(staleTime.Value));

        return options.Build();
    }

    private static (QueryClient Client, PrerenderTransfer Transfer, ServiceProvider Provider) Host(
        Microsoft.AspNetCore.Components.PersistentComponentState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope().ServiceProvider;

        // Resolving the transfer first is what registers its persisting callback.
        var transfer = scope.GetRequiredService<PrerenderTransfer>();

        return (scope.GetRequiredService<QueryClient>(), transfer, provider);
    }

    [Fact]
    public async Task A_Prerendered_Query_Reaches_The_Client_Without_A_Second_Fetch()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        var serverState = PersistentStateHarness.CreateEmpty(out var written);
        var (server, _, serverProvider) = Host(serverState);

        var fetched = await server.FetchQueryAsync(ServerQuery("widget"));
        Assert.Equal(new Widget(7, "from server"), fetched);
        Assert.Equal(1, _serverCalls);

        await PersistentStateHarness.PersistAsync(serverState);
        Assert.NotEmpty(written);

        // The server's scope is gone. A WebAssembly client boots with an empty registry and only
        // what the prerender sent.
        serverProvider.Dispose();

        var clientState = PersistentStateHarness.CreateFrom(written);
        var (client, _, clientProvider) = Host(clientState);

        // A stale time is what makes transferred data worth reusing. With the default of zero
        // every query is stale the moment it lands, so the transfer would remove the loading
        // flash but still refetch, exactly as TanStack does with staleTime 0.
        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });
        await Task.Delay(300);

        Assert.Equal(new Widget(7, "from server"), observer.Query.Data);
        Assert.Equal(0, _clientCalls);

        observer.Dispose();
        clientProvider.Dispose();
    }

    [Fact]
    public async Task Stale_Transferred_Data_Is_Shown_And_Then_Refetched()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        var serverState = PersistentStateHarness.CreateEmpty(out var written);
        var (server, _, serverProvider) = Host(serverState);

        await server.FetchQueryAsync(ServerQuery("widget"));
        await PersistentStateHarness.PersistAsync(serverState);
        serverProvider.Dispose();

        // Rewrite the transferred timestamp to long ago, which is what a slow prerender or a
        // client that took its time booting produces.
        var payload = System.Text.Encoding.UTF8.GetString(written[TransferStateKey()]);
        written[TransferStateKey()] = System.Text.Encoding.UTF8.GetBytes(
            payload.Replace(FindTimestamp(payload), "2020-01-01T00:00:00+00:00"));

        var clientState = PersistentStateHarness.CreateFrom(written);
        var (client, _, clientProvider) = Host(clientState);

        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });

        await Task.Delay(400);

        // The transfer landed and was then replaced, because the data it carried was older than
        // the stale time. Restoring LastUpdatedAt verbatim is what makes that decision possible.
        Assert.Equal(1, _clientCalls);
        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);

        observer.Dispose();
        clientProvider.Dispose();
    }

    [Fact]
    public async Task A_Failed_Query_Is_Not_Transferred()
    {
        _clientCalls = 0;

        var serverState = PersistentStateHarness.CreateEmpty(out var written);
        var (server, _, serverProvider) = Host(serverState);

        await Assert.ThrowsAnyAsync<Exception>(() => server.FetchQueryAsync(
            QueryOptions.Create("widget", static _ =>
                Task.FromException<Widget>(new InvalidOperationException("boom"))).Build()));

        await PersistentStateHarness.PersistAsync(serverState);
        serverProvider.Dispose();

        // Nothing resolved, so nothing to send. The client fetches for itself.
        Assert.Empty(written);

        var clientState = PersistentStateHarness.CreateFrom(written);
        var (client, _, clientProvider) = Host(clientState);

        var observer = client.Subscribe(ClientQuery("widget"), () => { });
        await Task.Delay(300);

        Assert.Equal(1, _clientCalls);
        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);

        observer.Dispose();
        clientProvider.Dispose();
    }

    [Fact]
    public async Task Keys_That_Differ_Only_By_Segment_Type_Or_Punctuation_Do_Not_Collide()
    {
        var serverState = PersistentStateHarness.CreateEmpty(out var written);
        var (server, _, serverProvider) = Host(serverState);

        // ("k", "1") and ("k", 1) print the same; ("a/b", "c") and ("a", "b/c") join the same.
        // The encoding has to keep all four apart or one query serves another's data.
        await server.FetchQueryAsync(Named(("k", "1"), "text one"));
        await server.FetchQueryAsync(Named(("k", 1), "number one"));
        await server.FetchQueryAsync(Named(("a/b", "c"), "slash left"));
        await server.FetchQueryAsync(Named(("a", "b/c"), "slash right"));

        await PersistentStateHarness.PersistAsync(serverState);
        serverProvider.Dispose();

        var clientState = PersistentStateHarness.CreateFrom(written);
        var (client, _, clientProvider) = Host(clientState);

        Assert.Equal("text one", await Restored(client, ("k", "1")));
        Assert.Equal("number one", await Restored(client, ("k", 1)));
        Assert.Equal("slash left", await Restored(client, ("a/b", "c")));
        Assert.Equal("slash right", await Restored(client, ("a", "b/c")));

        clientProvider.Dispose();
    }

    private static QueryOptions<TKey, Widget> Named<TKey>(TKey key, string name)
        where TKey : System.Runtime.CompilerServices.ITuple =>
        QueryOptions.Create<TKey, Widget>(key, _ => Task.FromResult(new Widget(0, name))).Build();

    /// <summary>
    /// Subscribes with a handler that would be obvious if it ran, waits for the restore, and
    /// returns whatever name the query ended up holding.
    /// </summary>
    private static async Task<string?> Restored<TKey>(QueryClient client, TKey key)
        where TKey : System.Runtime.CompilerServices.ITuple
    {
        var options = QueryOptions
            .Create<TKey, Widget>(key, _ => Task.FromResult(new Widget(0, "refetched")))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build();

        var observer = client.Subscribe(options, () => { });
        await Task.Delay(200);
        var name = observer.Query.Data?.Name;
        observer.Dispose();

        return name;
    }

    [Fact]
    public void Registration_Without_A_TypeInfoResolver_Fails_Loud()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(
            () => services.AddRevalQueryPrerenderTransfer(new JsonSerializerOptions()));

        Assert.Contains("TypeInfoResolver", error.Message);
    }

    [Fact]
    public async Task The_Transfer_Joins_A_Persistence_Store_Rather_Than_Replacing_It()
    {
        var store = new RecordingPersistence();

        var services = new ServiceCollection();
        services.AddSingleton(PersistentStateHarness.CreateEmpty(out _));
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);
        services.AddScoped<IQueryPersistence>(_ => store);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope().ServiceProvider;
        var client = scope.GetRequiredService<QueryClient>();

        await client.FetchQueryAsync(ServerQuery("widget"));
        await Task.Delay(200);

        // The transfer takes the IQueryPersistence slot too, so a store registered alongside it
        // has to still see the save.
        Assert.Equal(1, store.Saves);

        provider.Dispose();
    }

    private static string TransferStateKey() => "RevalQuery.PrerenderTransfer";

    private static string FindTimestamp(string payload)
    {
        var marker = "\"LastUpdatedAt\":\"";
        var start = payload.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = payload.IndexOf('"', start);
        return payload[start..end];
    }

    private sealed class RecordingPersistence : IQueryPersistence
    {
        public int Saves;

        public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, CancellationToken ct = default) =>
            new((PersistedQuery<TRes>?)null);

        public ValueTask SaveAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Saves);
            return default;
        }
    }
}
