using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Blazor;
using RevalQuery.Blazor.Prerender;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Blazor.Tests;

public sealed record Widget(int Id, string Name);

[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(string))]
internal sealed partial class WidgetContext : JsonSerializerContext;

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

    private static Hosted Host(Microsoft.AspNetCore.Components.PersistentComponentState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        return new Hosted(services.BuildServiceProvider());
    }

    private sealed class Hosted : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public Hosted(ServiceProvider provider)
        {
            _provider = provider;
            _scope = provider.CreateScope();
            Transfer = _scope.ServiceProvider.GetRequiredService<PrerenderTransfer>();
            Client = _scope.ServiceProvider.GetRequiredService<RevalClient>();
        }

        public RevalClient Client { get; }

        public PrerenderTransfer Transfer { get; }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    [Fact]
    public async Task A_Prerendered_Query_Reaches_The_Client_Without_A_Second_Fetch()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        var fetched = await server.QueryAsync(ServerQuery("widget"));
        Assert.Equal(new Widget(7, "from server"), fetched);
        Assert.Equal(1, _serverCalls);

        var written = await serverState.PersistAsync();
        Assert.NotEmpty(written);

        serverHost.Dispose();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });
        await Task.Delay(300);

        Assert.Equal(new Widget(7, "from server"), observer.Query.Data);
        Assert.Equal(0, _clientCalls);

        observer.Dispose();
        clientHost.Dispose();
    }

    [Fact]
    public async Task An_Invalidation_From_The_Prerender_Crosses_To_The_Client()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        await server.QueryAsync(ServerQuery("widget"));

        server.Invalidate("widget");

        var written = await serverState.PersistAsync();
        serverHost.Dispose();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });

        await TestUtils.UntilAsync(() => Volatile.Read(ref _clientCalls) > 0);

        Assert.Equal(1, _clientCalls);
        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);

        observer.Dispose();
        clientHost.Dispose();
    }

    [Fact]
    public async Task A_Transfer_Without_An_Invalidation_Is_Reused()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        await server.QueryAsync(ServerQuery("widget"));
        var written = await serverState.PersistAsync();
        serverHost.Dispose();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });
        await Task.Delay(300);

        Assert.Equal(0, _clientCalls);
        Assert.Equal(new Widget(7, "from server"), observer.Query.Data);

        observer.Dispose();
        clientHost.Dispose();
    }

    [Fact]
    public async Task A_Corrupt_Outer_Payload_Counts_As_Nothing_Arrived()
    {
        _clientCalls = 0;

        var corrupt = new Dictionary<string, byte[]>
        {
            [TransferStateKey()] = System.Text.Encoding.UTF8.GetBytes("{ this is not json"),
        };

        using var clientState = await PersistentStateHarness.CreateFromAsync(corrupt);
        using var clientHost = Host(clientState.State);

        var loaded = await clientHost.Transfer.LoadAsync<Widget>(ValueTuple.Create("widget"));

        Assert.Null(loaded);

        var observer = clientHost.Client.Subscribe(ClientQuery("widget"), () => { });
        await clientHost.Client.FindQuery<Widget, ValueTuple<string>>(ValueTuple.Create("widget"))!.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(30));

        await RevalQuery.TestKit.Signals.UntilAsync(observer.Query, s => s.IsIdle && s.Data is not null);

        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);
        Assert.Equal(1, _clientCalls);

        observer.Dispose();
    }

    [Fact]
    public async Task Stale_Transferred_Data_Is_Shown_And_Then_Refetched()
    {
        _serverCalls = 0;
        _clientCalls = 0;

        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        await server.QueryAsync(ServerQuery("widget"));
        var written = await serverState.PersistAsync();
        serverHost.Dispose();

        var payload = System.Text.Encoding.UTF8.GetString(written[TransferStateKey()]);
        written[TransferStateKey()] = System.Text.Encoding.UTF8.GetBytes(
            payload.Replace(FindTimestamp(payload), "2020-01-01T00:00:00+00:00"));

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        var observer = client.Subscribe(ClientQuery("widget", TimeSpan.FromMinutes(5)), () => { });

        await TestUtils.UntilAsync(() => Volatile.Read(ref _clientCalls) > 0);

        Assert.Equal(1, _clientCalls);
        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);

        observer.Dispose();
        clientHost.Dispose();
    }

    [Fact]
    public async Task A_Failed_Query_Is_Not_Transferred()
    {
        _clientCalls = 0;

        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        await Assert.ThrowsAnyAsync<Exception>(() => server.QueryAsync(
            QueryOptions.Create("widget", static _ =>
                Task.FromException<Widget>(new InvalidOperationException("boom"))).Build()));

        var written = await serverState.PersistAsync();
        serverHost.Dispose();

        Assert.Empty(written);

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        var observer = client.Subscribe(ClientQuery("widget"), () => { });

        await TestUtils.UntilAsync(() => observer.Query.Data is not null, timeoutMs: 15_000);

        Assert.Equal(1, _clientCalls);
        Assert.Equal(new Widget(9, "from client"), observer.Query.Data);

        observer.Dispose();
        clientHost.Dispose();
    }

    [Fact]
    public async Task Keys_That_Differ_Only_By_Segment_Type_Or_Punctuation_Do_Not_Collide()
    {
        using var serverState = await PersistentStateHarness.CreateEmptyAsync();
        using var serverHost = Host(serverState.State);
        var server = serverHost.Client;

        await server.QueryAsync(Named(("k", "1"), "text one"));
        await server.QueryAsync(Named(("k", 1), "number one"));
        await server.QueryAsync(Named(("a/b", "c"), "slash left"));
        await server.QueryAsync(Named(("a", "b/c"), "slash right"));

        var written = await serverState.PersistAsync();
        serverHost.Dispose();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        Assert.Equal("text one", await Restored(client, ("k", "1")));
        Assert.Equal("number one", await Restored(client, ("k", 1)));
        Assert.Equal("slash left", await Restored(client, ("a/b", "c")));
        Assert.Equal("slash right", await Restored(client, ("a", "b/c")));

        clientHost.Dispose();
    }

    private static QueryOptions<TKey, Widget> Named<TKey>(TKey key, string name)
        where TKey : System.Runtime.CompilerServices.ITuple =>
        QueryOptions.Create<TKey, Widget>(key, _ => Task.FromResult(new Widget(0, name))).Build();

    private static async Task<string?> Restored<TKey>(RevalClient client, TKey key)
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
        using var harness = await PersistentStateHarness.CreateEmptyAsync();

        var services = new ServiceCollection();
        services.AddSingleton(harness.State);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);
        services.AddScoped<IQueryPersistence>(_ => store);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        await client.QueryAsync(ServerQuery("widget"));
        await Task.Delay(200);

        Assert.Equal(1, store.Saves);
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
