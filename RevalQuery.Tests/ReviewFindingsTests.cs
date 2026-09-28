using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Tests;

/// <summary>
/// Cases the review turned up that the rest of the suite did not reach.
/// </summary>
/// <remarks>
/// Three of these fail against the code as it stood before the fix: the wedged query, the
/// suppressed fetch, and the overfull death row. The rest cover interleavings too narrow to
/// provoke on demand, so they are guards against regression rather than reproductions.
/// </remarks>
public class ReviewFindingsTests
{
    /// <summary>
    /// An observer that throws, standing in for a Blazor component whose circuit went away
    /// while a background fetch was running.
    /// </summary>
    [Fact]
    public async Task An_Observer_That_Throws_Does_Not_Wedge_The_Query()
    {
        using var client = NewClient();
        var calls = 0;

        var options = QueryOptions.Create("wedged", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("ok");
        }).Build();

        using var observer = client.Subscribe(options, () => throw new ObjectDisposedException("circuit"));

        await WaitUntil(() => Volatile.Read(ref calls) == 1);

        // The first fetch's notifications all threw. The query still has to be runnable.
        client.Invalidate("wedged");
        await WaitUntil(() => Volatile.Read(ref calls) == 2);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(client.FindQuery("wedged")!.IsIdle);
    }

    [Fact]
    public async Task FetchQueryAsync_Does_Not_Return_Data_It_Never_Fetched()
    {
        using var client = NewClient();

        // Release the worker under the fetch, the way a concurrent prefetch settling would.
        var options = QueryOptions.Create("released", async ctx =>
        {
            await Task.Delay(30, ctx.CancellationToken ?? CancellationToken.None);
            return "value";
        }).Build();

        var fetch = client.FetchQueryAsync(options);
        await Task.Delay(5);
        client.PrefetchQuery(options);

        Assert.Equal("value", await fetch);
    }

    [Fact]
    public async Task Resubscribing_While_The_Previous_Observer_Leaves_Keeps_Polling()
    {
        using var client = NewClient();
        var calls = 0;

        var options = QueryOptions.Create("polled", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("ok");
        }).ConfigureFetch(f => f.RefetchInterval(TimeSpan.FromMilliseconds(30))).Build();

        for (var round = 0; round < 20; round++)
        {
            var first = client.Subscribe(options, () => { });
            var second = client.Subscribe(options, () => { });
            first.Dispose();
            second.Dispose();
        }

        var observer = client.Subscribe(options, () => { });
        var before = Volatile.Read(ref calls);

        await WaitUntil(() => Volatile.Read(ref calls) >= before + 3);

        Assert.True(Volatile.Read(ref calls) >= before + 3, "polling stopped after the churn");
        observer.Dispose();
    }

    [Fact]
    public async Task Fresh_Persisted_Data_Suppresses_The_Fetch()
    {
        // A delay makes the load genuinely asynchronous, which is the case that matters: a
        // synchronous adapter would land before the stale check ran no matter what.
        var persistence = new StubPersistence(
            new PersistedQuery<string>("from-disk", DateTimeOffset.UtcNow), loadDelayMs: 30);
        using var client = NewClient(persistence);

        var calls = 0;
        var options = QueryOptions.Create("fresh", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("from-network");
        }).ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5))).Build();

        using var observer = client.Subscribe(options, () => { });

        await WaitUntil(() => observer.Query.IsResolved);
        await Task.Delay(100);

        Assert.Equal("from-disk", observer.Query.Data);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Stale_Persisted_Data_Still_Refetches()
    {
        var persistence = new StubPersistence(
            new PersistedQuery<string>("from-disk", DateTimeOffset.UtcNow.AddHours(-3)), loadDelayMs: 30);
        using var client = NewClient(persistence);

        var options = QueryOptions.Create("stale", _ => Task.FromResult("from-network"))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build();

        using var observer = client.Subscribe(options, () => { });

        await WaitUntil(() => observer.Query.Data == "from-network");

        Assert.Equal("from-network", observer.Query.Data);
    }

    [Fact]
    public async Task A_Late_Restore_Never_Overwrites_A_Landed_Fetch()
    {
        var persistence = new StubPersistence(
            new PersistedQuery<string>("from-disk", DateTimeOffset.UtcNow.AddHours(-3)),
            loadDelayMs: 120);
        using var client = NewClient(persistence);

        var options = QueryOptions.Create("late", _ => Task.FromResult("from-network")).Build();

        await client.FetchQueryAsync(options);
        var landedAt = client.FindQuery("late")!.LastUpdatedAt;

        await Task.Delay(250);

        var state = client.FindQuery<string>("late")!;
        Assert.Equal("from-network", state.Data);
        Assert.Equal(landedAt, state.LastUpdatedAt);
    }

    [Fact]
    public void An_Overfull_Death_Row_Evicts_Rather_Than_Forgetting()
    {
        var options = new RevalQueryOptions
        {
            CacheOptions = new RevalQuery.Core.Configuration.Options.CoreCacheOptions(
                GcTime: TimeSpan.FromHours(1),
                GcInterval: TimeSpan.FromHours(1))
        };

        var policy = new TtlQueryGarbageCollector(options);
        var evicted = new List<ITuple>();
        policy.OnEvictionRequired += key => evicted.Add(key);

        for (var i = 0; i < 10_002; i++) policy.RegisterForEviction(ValueTuple.Create($"k{i}"), null);

        // Nothing has expired yet, so the overflow relief is the only thing that can fire.
        Assert.NotEmpty(evicted);
        policy.Dispose();
    }

    // ---------- helpers ----------

    private static QueryClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private sealed class StubPersistence(PersistedQuery<string> stored, int loadDelayMs = 0) : IQueryPersistence
    {
        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
        {
            if (loadDelayMs > 0) await Task.Delay(loadDelayMs, ct);
            return (PersistedQuery<TRes>?)(object)stored;
        }

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
            default;
    }
}
