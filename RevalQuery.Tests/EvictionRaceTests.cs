using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Tests;

[CollectionDefinition("stress", DisableParallelization = true)]
public sealed class StressCollection;

[Collection("stress")]
public class EvictionRaceTests
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
            await Task.Delay(5);
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
                    await TestUtils.WaitUntilAsync(() => observer.Query.Data is not null, timeoutMs: 1000);
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
            await storm;
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

    private static void AssertEveryWorkerHasItsState(RevalClient client)
    {
        var registry = (QueryRegistry)typeof(RevalClient)
            .GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;

        Walk(registry.Root);

        static void Walk(RegistryNode node)
        {
            Assert.False(
                node.Worker is not null && node.State is null,
                "a registry node holds a worker but no state, so the next subscriber to that " +
                "key would get a state nothing is driving");

            foreach (var child in node.Children.Values) Walk(child);
        }
    }
}
