using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;
using static RevalQuery.Core.Tests.RegistryProbe;

namespace RevalQuery.Core.Tests;

[CollectionDefinition("stress", DisableParallelization = true)]
public sealed class StressCollection;

public class EvictionRaceTests
{
    [Fact]
    public async Task A_Retry_After_An_Eviction_Takes_A_Fresh_State_And_Worker()
    {
        var eviction = new ManualEviction();
        using var client = new RevalClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);
        var handler = new GatedHandler<string>(honourToken: false);
        var options = QueryOptions.Create("raced", handler.Handle).Build();

        var superseded = client.QueryAsync(options);
        Assert.Equal(1, handler.Calls);
        client.FindQuery("raced")!.Cancel();

        var retried = client.QueryAsync(options);
        Assert.Equal(1, handler.Calls);

        eviction.Evict(ValueTuple.Create("raced"));
        AssertEveryWorkerHasItsState(client);

        handler.Release(0, "stale");
        await handler.Started(1).WaitAsync(HangGuard);
        AssertEveryWorkerHasItsState(client);
        Assert.NotNull(client.FindQuery("raced"));

        handler.Release(1, "from-network");
        Assert.Equal("from-network", await retried.WaitAsync(HangGuard));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => superseded.WaitAsync(HangGuard));

        AssertEveryWorkerHasItsState(client);
        Assert.Equal("from-network", client.FindQuery<string>("raced")!.Data);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, WorkerCount(client));
    }

    [Fact]
    public async Task A_Subscriber_After_An_Eviction_Mid_Fetch_Gets_A_Driven_State()
    {
        var eviction = new ManualEviction();
        using var client = new RevalClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("raced", handler.Handle).Build();

        var fetch = client.QueryAsync(options);
        Assert.Equal(1, handler.Calls);

        eviction.Evict(ValueTuple.Create("raced"));
        AssertEveryWorkerHasItsState(client);

        using var observer = client.Subscribe(options, () => { });
        Assert.Same(observer.Query, client.FindQuery("raced"));
        Assert.True(observer.Query.IsFetching);
        AssertEveryWorkerHasItsState(client);

        var resolved = Signals.UntilAsync(observer.Query, q => q.IsResolved);
        handler.ReleaseAll("from-network");
        await resolved;

        Assert.Equal("from-network", observer.Query.Data);
        AssertEveryWorkerHasItsState(client);
        Discard(fetch);
    }
}

/// <summary>
/// Probabilistic regression net for the eviction race the deterministic tests in
/// <see cref="EvictionRaceTests"/> pin one interleaving at a time. A storm can find an
/// interleaving nobody wrote down, but it cannot prove one is absent.
/// </summary>
[Collection("stress")]
public class EvictionRaceStressTests
{
    private const int StormRounds = 300;

    [Fact]
    public async Task An_Eviction_Between_Run_Attempts_Never_Strands_A_Worker_On_A_Stateless_Node()
    {
        var eviction = new EvictOnRelease();
        using var client = new RevalClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        var options = QueryOptions.Create<string>("raced", async _ =>
        {
            await Task.Yield();
            return "from-network";
        }).Build();

        var rounds = 0;
        var storm = Task.Run(async () =>
        {
            while (Volatile.Read(ref rounds) < StormRounds)
            {
                var fetches = Enumerable.Range(0, 8)
                    .Select(_ => Settle(client.QueryAsync(options)))
                    .ToArray();

                foreach (var result in await Task.WhenAll(fetches))
                {
                    if (result is not null) Assert.Equal("from-network", result);
                }
            }
        });

        try
        {
            while (Interlocked.Increment(ref rounds) < StormRounds)
            {
                var observer = client.Subscribe(options, () => { });

                try
                {
                    await Signals.UntilAsync(observer.Query, q => q.Data is not null);
                    Assert.Equal("from-network", observer.Query.Data);
                    AssertEveryWorkerHasItsState(client);
                }
                finally
                {
                    observer.Dispose();
                }
            }
        }
        finally
        {
            Volatile.Write(ref rounds, StormRounds);
            await storm.WaitAsync(HangGuard);
        }
    }

    private static async Task<string?> Settle(Task<string> fetch)
    {
        try
        {
            return await fetch;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class EvictOnRelease : ICacheEvictionPolicy
    {
        public event Action<ITuple>? OnEvictionRequired;

        public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) =>
            OnEvictionRequired?.Invoke(key);

        public void CancelEviction(ITuple key) { }

        public Task StopAsync() => Task.CompletedTask;
    }
}
