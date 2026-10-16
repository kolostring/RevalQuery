using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Blazor;
using RevalQuery.Blazor.Prerender;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using System.Text.RegularExpressions;

namespace RevalQuery.Blazor.Tests;

public sealed record Widget(int Id, string Name);

[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(string))]
internal sealed partial class WidgetContext : JsonSerializerContext;

public class PrerenderTransferTests
{
    private static readonly JsonSerializerOptions Serializer =
        new() { TypeInfoResolver = WidgetContext.Default };

    private static QueryOptions<ValueTuple<string>, Widget> ServerQuery(string key, Counter calls) =>
        QueryOptions.Create(key, _ =>
        {
            calls.Increment();
            return Task.FromResult(new Widget(7, "from server"));
        }).Build();

    private static QueryOptions<ValueTuple<string>, Widget> ClientQuery(string key, Counter calls, TimeSpan? staleTime = null) =>
        ClientQuery(key, _ =>
        {
            calls.Increment();
            return Task.FromResult(new Widget(9, "from client"));
        }, staleTime);

    private static QueryOptions<ValueTuple<string>, Widget> ClientQuery(
        string key, Func<QueryHandlerExecutionContext<ValueTuple<string>>, Task<Widget>> handler, TimeSpan? staleTime = null)
    {
        var options = QueryOptions.Create(key, handler);

        if (staleTime is not null) options.ConfigureFetch(f => f.StaleTime(staleTime.Value));

        return options.Build();
    }

    /// <summary>
    /// Subscribes, then waits for the restore to end. Once it has, everything the restore led to,
    /// including a fetch it decided on, has happened.
    /// </summary>
    private static async Task<(QueryObserver<ValueTuple<string>, Widget> Observer, QueryState<ValueTuple<string>, Widget> State)>
        SubscribeRestored(RevalClient client, QueryOptions<ValueTuple<string>, Widget> options)
    {
        var observer = client.Subscribe(options, () => { });
        var state = client.FindQuery<Widget>("widget")!;
        await state.RestoreCompleted.WaitAsync(HangGuard);

        return (observer, state);
    }

    private static Hosted Host(Microsoft.AspNetCore.Components.PersistentComponentState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        return new Hosted(services.BuildServiceProvider());
    }

    private static Hosted Host(Microsoft.AspNetCore.Components.PersistentComponentState state, IQueryPersistence store, bool storeFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();

        if (storeFirst) services.AddScoped<IQueryPersistence>(_ => store);
        services.AddRevalQueryPrerenderTransfer(Serializer);
        if (!storeFirst) services.AddScoped<IQueryPersistence>(_ => store);

        return new Hosted(services.BuildServiceProvider());
    }

    /// <summary>
    /// Runs the server half: serves the queries, and returns what the prerender wrote.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> Prerendered(
        Func<RevalClient, Task> serve, Action<RevalClient>? afterwards = null)
    {
        using var state = await PersistentStateHarness.CreateEmptyAsync();
        using var host = Host(state.State);

        await serve(host.Client);
        afterwards?.Invoke(host.Client);

        return await state.PersistAsync();
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
        var serverCalls = new Counter();
        var clientCalls = new Counter();

        var written = await Prerendered(async server =>
            Assert.Equal(new Widget(7, "from server"), await server.QueryAsync(ServerQuery("widget", serverCalls))));

        Assert.Equal(1, serverCalls.Value);
        Assert.NotEmpty(written);

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);

        var (observer, state) = await SubscribeRestored(
            clientHost.Client, ClientQuery("widget", clientCalls, TimeSpan.FromMinutes(5)));

        Assert.Equal(new Widget(7, "from server"), state.Data);
        Assert.Equal(0, clientCalls.Value);
        Assert.False(state.IsFetching);

        observer.Dispose();
    }

    [Fact]
    public async Task An_Invalidation_From_The_Prerender_Crosses_To_The_Client()
    {
        var clientCalls = new Counter();

        var written = await Prerendered(
            server => server.QueryAsync(ServerQuery("widget", new Counter())),
            server => server.Invalidate("widget"));

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);

        var (observer, state) = await SubscribeRestored(
            clientHost.Client, ClientQuery("widget", clientCalls, TimeSpan.FromMinutes(5)));

        Assert.Equal(1, clientCalls.Value);
        Assert.Equal(new Widget(9, "from client"), state.Data);

        observer.Dispose();
    }

    [Fact]
    public async Task A_Corrupt_Outer_Payload_Counts_As_Nothing_Arrived()
    {
        var clientCalls = new Counter();

        var corrupt = new Dictionary<string, byte[]>
        {
            [TransferStateKey] = System.Text.Encoding.UTF8.GetBytes("{ this is not json"),
        };

        using var clientState = await PersistentStateHarness.CreateFromAsync(corrupt);
        using var clientHost = Host(clientState.State);

        var loaded = await clientHost.Transfer.LoadAsync<Widget>(ValueTuple.Create("widget"));

        Assert.Null(loaded);

        var (observer, state) = await SubscribeRestored(
            clientHost.Client, ClientQuery("widget", clientCalls));

        await RevalQuery.TestKit.Signals.UntilAsync(observer.Query, s => s.IsIdle && s.Data is not null);

        Assert.Equal(new Widget(9, "from client"), state.Data);
        Assert.Equal(1, clientCalls.Value);

        observer.Dispose();
    }

    [Fact]
    public async Task Stale_Transferred_Data_Is_Shown_And_Then_Refetched()
    {
        var written = await Prerendered(server => server.QueryAsync(ServerQuery("widget", new Counter())));

        var payload = JsonSerializer.Deserialize<string>(written[TransferStateKey])!;
        var aged = LastUpdatedAt.Replace(payload, "\"LastUpdatedAt\":\"2020-01-01T00:00:00+00:00\"");
        Assert.NotEqual(payload, aged);
        written[TransferStateKey] = JsonSerializer.SerializeToUtf8Bytes(aged);

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var handler = new GatedHandler<Widget>();

        var (observer, state) = await SubscribeRestored(
            clientHost.Client, ClientQuery("widget", handler.Handle, TimeSpan.FromMinutes(5)));

        Assert.Equal(new Widget(7, "from server"), state.Data);
        Assert.True(state.IsFetching);
        Assert.Equal(1, handler.Calls);

        var refetched = Signals.UntilAsync(state, s => s.IsIdle && s.Data == new Widget(9, "from client"));
        handler.Release(0, new Widget(9, "from client"));
        await refetched;

        Assert.Equal(1, handler.Calls);

        observer.Dispose();
    }

    [Fact]
    public async Task A_Failed_Query_Is_Not_Transferred()
    {
        var clientCalls = new Counter();

        var written = await Prerendered(async server =>
        {
            await server.QueryAsync(ServerQuery("covered", new Counter()));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.QueryAsync(
                QueryOptions.Create("widget", _ => Task.FromException<Widget>(new InvalidOperationException("boom")))
                    .ConfigureRetry(r => r.Retry(0))
                    .Build()));

            Assert.Contains("boom", error.Message);
        });

        var payload = JsonSerializer.Deserialize<string>(written[TransferStateKey])!;
        Assert.Contains("covered", payload);
        Assert.DoesNotContain("widget", payload);

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);

        var (observer, state) = await SubscribeRestored(clientHost.Client, ClientQuery("widget", clientCalls));

        Assert.Equal(1, clientCalls.Value);
        Assert.Equal(new Widget(9, "from client"), state.Data);

        observer.Dispose();
    }

    [Fact]
    public async Task Keys_That_Differ_Only_By_Segment_Type_Or_Punctuation_Do_Not_Collide()
    {
        var refetches = new Counter();

        var written = await Prerendered(async server =>
        {
            await server.QueryAsync(Named(("k", "1"), "text one"));
            await server.QueryAsync(Named(("k", 1), "number one"));
            await server.QueryAsync(Named(("a/b", "c"), "slash left"));
            await server.QueryAsync(Named(("a", "b/c"), "slash right"));
        });

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var clientHost = Host(clientState.State);
        var client = clientHost.Client;

        Assert.Equal("text one", await Restored(client, ("k", "1"), refetches));
        Assert.Equal("number one", await Restored(client, ("k", 1), refetches));
        Assert.Equal("slash left", await Restored(client, ("a/b", "c"), refetches));
        Assert.Equal("slash right", await Restored(client, ("a", "b/c"), refetches));
        Assert.Equal(0, refetches.Value);
    }

    private static QueryOptions<TKey, Widget> Named<TKey>(TKey key, string name)
        where TKey : System.Runtime.CompilerServices.ITuple =>
        QueryOptions.Create<TKey, Widget>(key, _ => Task.FromResult(new Widget(0, name))).Build();

    private static async Task<string?> Restored<TKey>(RevalClient client, TKey key, Counter refetches)
        where TKey : System.Runtime.CompilerServices.ITuple
    {
        var options = QueryOptions
            .Create<TKey, Widget>(key, _ =>
            {
                refetches.Increment();
                return Task.FromResult(new Widget(0, "refetched"));
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build();

        var observer = client.Subscribe(options, () => { });
        await client.FindQuery<Widget, TKey>(key)!.RestoreCompleted.WaitAsync(HangGuard);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Transfer_Joins_A_Persistence_Store_Rather_Than_Replacing_It(bool storeFirst)
    {
        var written = await Prerendered(server => server.QueryAsync(ServerQuery("widget", new Counter())));

        var store = new RecordingPersistence();
        var clientCalls = new Counter();
        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var host = Host(clientState.State, store, storeFirst);

        var (observer, state) = await SubscribeRestored(
            host.Client, ClientQuery("widget", clientCalls, TimeSpan.FromMinutes(5)));

        Assert.Equal(new Widget(7, "from server"), state.Data);
        Assert.Equal(0, clientCalls.Value);
        // Registration order sets precedence: a store registered first is asked first, and misses.
        Assert.Equal(storeFirst ? 1 : 0, store.Loads);

        await host.Client.QueryAsync(ServerQuery("other", new Counter()));

        Assert.Equal(1, store.Saves);

        observer.Dispose();
    }

    private const string TransferStateKey = "RevalQuery.PrerenderTransfer";

    private static readonly Regex LastUpdatedAt = new("\"LastUpdatedAt\":\"[^\"]*\"");

    private sealed class RecordingPersistence : IQueryPersistence
    {
        public int Saves;
        public int Loads;

        public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Loads);
            return new((PersistedQuery<TRes>?)null);
        }

        public ValueTask SaveAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Saves);
            return default;
        }
    }
}
