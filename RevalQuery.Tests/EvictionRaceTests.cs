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

/// <summary>
/// Runs alone. Hitting the window below takes a hot loop across several threads, and one of
/// those starves any test beside it that waits on a clock.
/// </summary>
[CollectionDefinition("stress", DisableParallelization = true)]
public sealed class StressCollection;

/// <summary>
/// A query pruned by an eviction between two run attempts must not come back with a worker
/// bound to the state the eviction dropped.
/// </summary>
[Collection("stress")]
public class EvictionRaceTests
{
    /// <summary>
    /// Rounds the stress runs for. The race is a narrow window, so it is reached by repetition
    /// rather than by arranging it.
    /// </summary>
    private const int StormRounds = 300;

    [Fact]
    public async Task An_Eviction_Between_Run_Attempts_Never_Strands_A_Worker_On_A_Stateless_Node()
    {
        // Evicts the instant a query is left unobserved, which is the window a one-off fetch
        // releases itself in. A real TTL collector reaches the same state, just rarely.
        var eviction = new EvictOnRelease();
        using var client = new QueryClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        var options = QueryOptions.Create<string>("raced", async _ =>
        {
            await Task.Delay(5);
            return "from-network";
        }).Build();

        // Two things at once, which is what the defect needs. The storm is one-off fetches,
        // which own no observer, so each release disposes the worker and a caller that took it
        // a moment earlier gets NotRun and retries. The eviction lands in that retry.
        var rounds = 0;
        var storm = Task.Run(async () =>
        {
            while (Volatile.Read(ref rounds) < StormRounds)
            {
                var fetches = Enumerable.Range(0, 8)
                    .Select(_ => Settle(client.QueryAsync(options)))
                    .ToArray();

                // An eviction this eager can dispose a worker out from under a fetch, so a
                // cancelled one is a fair outcome. Returning null while reporting success is
                // not: that is a caller handed the data of a query it was never bound to.
                foreach (var result in await Task.WhenAll(fetches))
                {
                    if (result is not null) Assert.Equal("from-network", result);
                }
            }
        });

        // Subscribing is what makes the defect bite. A node left holding a worker and no state
        // hands the next subscriber a second state for the same key, driven by nothing, while
        // the worker it was given goes on fetching into the first. The subscriber waits forever.
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

    /// <summary>
    /// Runs a fetch to its conclusion, reporting a cancelled one as null rather than throwing.
    /// </summary>
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

    /// <summary>
    /// Sampled between rounds, when nothing is mid-registration. A node holding a worker and no
    /// state is the shape the defect leaves behind.
    /// </summary>
    private static void AssertEveryWorkerHasItsState(QueryClient client)
    {
        var registry = (QueryRegistry)typeof(QueryClient)
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
