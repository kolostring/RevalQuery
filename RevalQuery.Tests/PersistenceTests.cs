using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Tests;

public class PersistenceTests
{
    [Fact]
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

    [Fact]
    public async Task PersistedData_Is_Restored_Before_The_Fetch_Lands()
    {
        var fetchedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var persistence = new InMemoryPersistence();
        persistence.Write(ValueTuple.Create("restored"), new PersistedQuery<string>("from-disk", new QueryFreshness(fetchedAt)));

        using var client = NewClient(persistence);
        using var handlerGate = new SemaphoreSlim(0);

        var options = QueryOptions.Create<string>("restored", async ctx =>
        {
            await handlerGate.WaitAsync(ctx.CancellationToken ?? CancellationToken.None);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });

        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        Assert.Equal("from-disk", observer.Query.Data);
        // Restored verbatim, so the data is correctly three hours stale rather than looking fresh.
        Assert.Equal(fetchedAt, observer.Query.LastUpdatedAt);

        handlerGate.Release();
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsIdle && s.LastUpdatedAt > fetchedAt);
        Assert.Equal("from-network", observer.Query.Data);
    }

    [Fact]
    public async Task A_Fetch_That_Already_Landed_Is_Not_Overwritten_By_The_Load()
    {
        var persistence = new SlowPersistence(
            new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow.AddHours(-3))));

        using var client = NewClient(persistence);

        await ClientFetch(client, "raced", "from-network");

        await persistence.AllowLoad();

        Assert.Equal("from-network", ((RevalQuery.Core.Query.QueryState<ValueTuple<string>, string>)
            client.FindQuery("raced")!).Data);
    }

    [Fact]
    public async Task A_Failing_Adapter_Leaves_The_Query_Working()
    {
        using var client = NewClient(new ThrowingPersistence());

        var state = await ClientFetch(client, "resilient", "from-handler");

        Assert.Equal("from-handler", state.Data);
        Assert.True(state.IsResolved);
    }

    [Fact]
    public void Persistence_Is_Optional_In_DI()
    {
        var sp = new ServiceCollection().AddRevalQuery().BuildServiceProvider();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<QueryClient>());
    }

    [Fact]
    public async Task A_Registered_Adapter_Is_Picked_Up_By_The_Client()
    {
        var persistence = new InMemoryPersistence();
        var sp = new ServiceCollection()
            .AddSingleton<IQueryPersistence>(persistence)
            .AddRevalQuery()
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<QueryClient>();

        await ClientFetch(client, "wired", "from-handler");

        Assert.NotNull(persistence.Read<string>(ValueTuple.Create("wired")));
    }

    // ---------- helpers ----------

    private static QueryClient NewClient(IQueryPersistence persistence) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);

    private static async Task<RevalQuery.Core.Query.QueryState<ValueTuple<string>, string>> ClientFetch(
        QueryClient client, string key, string result)
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

    private sealed class SlowPersistence(PersistedQuery<string> stored) : IQueryPersistence
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loadReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AllowLoad()
        {
            _gate.TrySetResult();
            await _loadReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
        }

        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
        {
            await _gate.Task;

            try
            {
                return (PersistedQuery<TRes>?)(object)stored;
            }
            finally
            {
                _loadReturned.TrySetResult();
            }
        }

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
            default;
    }

    private sealed class ThrowingPersistence : IQueryPersistence
    {
        public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default) =>
            throw new InvalidOperationException("adapter is down");

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("adapter is down");
    }
}
