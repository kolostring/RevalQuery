using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Tests;

public class PersistenceTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task SuccessfulFetch_Is_Saved_With_Its_Timestamp()
    {
        var persistence = new InMemoryPersistence();
        using var client = NewClient(persistence);

        var state = await ClientFetch(client, "saved", "from-handler");

        var entry = persistence.Read<string>(ValueTuple.Create("saved"));
        Assert.NotNull(entry);
        Assert.Equal("from-handler", entry.Data);
        Assert.Equal(state.LastUpdatedAt, entry.Freshness.LastUpdatedAt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task PersistedData_Is_Restored_Before_The_Fetch_Lands()
    {
        var fetchedAt = _clock.GetUtcNow().AddHours(-3);
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("restored", handler.Handle).Build();

        var observer = client.Subscribe(options, () => { });
        var state = client.FindQuery<string>("restored")!;
        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(state.IsRestoring);
        Assert.Equal(0, handler.Calls);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(fetchedAt)));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("from-disk", state.Data);
        Assert.Equal(fetchedAt, state.LastUpdatedAt);
        Assert.True(state.IsResolved);
        Assert.True(state.IsFetching);
        Assert.True(handler.Started(0).IsCompleted);

        handler.Release(0, "from-network");
        await Signals.UntilAsync(state, s => s.IsIdle && s.Data == "from-network", TestContext.Current.CancellationToken);

        Assert.True(state.LastUpdatedAt > fetchedAt);
        observer.Dispose();
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Fetch_That_Already_Landed_Is_Not_Overwritten_By_The_Load()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);

        var state = await ClientFetch(client, "raced", "from-network");
        var landedAt = state.LastUpdatedAt;

        Assert.True(state.IsRestoring);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(_clock.GetUtcNow().AddHours(-3))));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("from-network", state.Data);
        Assert.Equal(landedAt, state.LastUpdatedAt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Failing_Adapter_Leaves_The_Query_Working()
    {
        using var client = NewClient(new ThrowingPersistence());

        var state = await ClientFetch(client, "resilient", "from-handler");

        Assert.Equal("from-handler", state.Data);
        Assert.True(state.IsResolved);

        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(state.IsRestoring);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Persistence_Is_Optional_In_DI()
    {
        var sp = new ServiceCollection().AddRevalQuery().BuildServiceProvider();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RevalClient>());
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Registered_Adapter_Is_Picked_Up_By_The_Client()
    {
        var persistence = new InMemoryPersistence();
        var sp = new ServiceCollection()
            .AddSingleton<IQueryPersistence>(persistence)
            .AddRevalQuery()
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        await ClientFetch(client, "wired", "from-handler");

        Assert.NotNull(persistence.Read<string>(ValueTuple.Create("wired")));
    }

    private RevalClient NewClient(IQueryPersistence persistence) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, persistence: persistence);

    private static async Task<RevalQuery.Core.Query.QueryState<ValueTuple<string>, string>> ClientFetch(
        RevalClient client, string key, string result)
    {
        var options = QueryOptions.Create(key, _ => Task.FromResult(result)).Build();
        await client.QueryAsync(options);
        return client.FindQuery<string>(key)!;
    }

    private sealed class InMemoryPersistence : IQueryPersistence
    {
        private readonly ConcurrentDictionary<ITuple, object> _entries = new(QueryKeyComparer.Instance);

        public PersistedQuery<TRes>? Read<TRes>(ITuple key) =>
            _entries.TryGetValue(key, out var entry) ? (PersistedQuery<TRes>)entry : null;

        public void Write<TRes>(ITuple key, PersistedQuery<TRes> entry) => _entries[key] = entry;

        public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default) =>
            new(Read<TRes>(key));

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
        {
            Write(key, entry);
            return default;
        }
    }

    private sealed class ThrowingPersistence : IQueryPersistence
    {
        public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default) =>
            throw new InvalidOperationException("adapter is down");

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("adapter is down");
    }
}
