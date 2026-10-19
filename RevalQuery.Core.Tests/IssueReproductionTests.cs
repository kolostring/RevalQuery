using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;
using RevalQuery.TestKit;
using static RevalQuery.Core.Tests.RegistryProbe;
using static RevalQuery.TestKit.Kit;

namespace RevalQuery.Core.Tests;

public class IssueReproductionTests
{
    private static Task<string> Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("result");

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Issue1_EvictionPolicy_Is_Scoped_Alongside_RevalClient()
    {
        var sp = new ServiceCollection().AddRevalQuery().BuildServiceProvider();

        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var c1 = scope1.ServiceProvider.GetRequiredService<RevalClient>();
        var c2 = scope2.ServiceProvider.GetRequiredService<RevalClient>();

        Assert.NotSame(c1, c2);
        Assert.NotSame(
            scope1.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>(),
            scope2.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>());

        var services = new ServiceCollection().AddRevalQuery();
        Assert.All(
            services.Where(d => d.ServiceType == typeof(ICacheEvictionPolicy) || d.ServiceType == typeof(RevalClient)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue1_Eviction_In_One_Scope_Leaves_Another_Scope_Alone()
    {
        var sp = new ServiceCollection()
            .AddRevalQuery(o => o.CacheOptions = o.CacheOptions with { GcTime = TimeSpan.Zero })
            .BuildServiceProvider();

        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var c1 = scope1.ServiceProvider.GetRequiredService<RevalClient>();
        var c2 = scope2.ServiceProvider.GetRequiredService<RevalClient>();
        var gc1 = (TtlQueryGarbageCollector)scope1.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>();

        var opts = QueryOptions.Create("shared-key", Handler).Build();

        var o1 = c1.Subscribe(opts, () => { });
        var o2 = c2.Subscribe(opts, () => { });
        Assert.True(o1.Query.IsResolved);
        Assert.True(o2.Query.IsResolved);

        o1.Dispose();
        o2.Dispose();

        gc1.CollectExpiredEntries();

        Assert.Null(c1.FindQuery("shared-key"));
        Assert.NotNull(c2.FindQuery("shared-key"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue1_Disposing_A_Scope_Detaches_Its_Client_From_The_Eviction_Policy()
    {
        var sp = new ServiceCollection()
            .AddRevalQuery(o => o.CacheOptions = o.CacheOptions with { GcTime = TimeSpan.Zero })
            .BuildServiceProvider();

        for (var i = 0; i < 5; i++)
        {
            var scope = sp.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<RevalClient>();
            var gc = (TtlQueryGarbageCollector)scope.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>();

            var observer = client.Subscribe(QueryOptions.Create($"k{i}", Handler).Build(), () => { });
            Assert.True(observer.Query.IsResolved);
            observer.Dispose();

            Assert.Equal(1, EvictionSubscriberCount(gc));

            scope.Dispose();

            Assert.Equal(0, EvictionSubscriberCount(gc));
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Issue2_BackgroundEviction_Runs_Without_A_Manual_Start()
    {
        var time = new FakeTimeProvider();
        var sp = new ServiceCollection()
            .AddRevalQuery(o =>
            {
                o.TimeProvider = time;
                o.CacheOptions = new CoreCacheOptions(
                    GcTime: TimeSpan.FromMilliseconds(10),
                    GcInterval: TimeSpan.FromMilliseconds(20));
            })
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        var observer = client.Subscribe(QueryOptions.Create("gc-key", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);
        observer.Dispose();

        time.Advance(TimeSpan.FromMilliseconds(19));
        Assert.NotNull(client.FindQuery("gc-key"));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(client.FindQuery("gc-key"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Released_Query_Survives_Until_The_Clock_Passes_GcTime_And_The_Next_Sweep()
    {
        var time = new FakeTimeProvider();
        var sp = new ServiceCollection()
            .AddRevalQuery(o =>
            {
                o.TimeProvider = time;
                o.CacheOptions = new CoreCacheOptions(
                    GcTime: TimeSpan.FromSeconds(30),
                    GcInterval: TimeSpan.FromSeconds(60));
            })
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        var observer = client.Subscribe(QueryOptions.Create("gc-key", Handler).Build(), () => { });
        observer.Dispose();

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(client.FindQuery("gc-key"));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(client.FindQuery("gc-key"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Issue2_EvictionPolicy_Needs_No_Host_To_Start()
    {
        var services = new ServiceCollection().AddRevalQuery();

        Assert.DoesNotContain(services, d => d.ServiceType.Name.Contains("IHostedService"));
        Assert.Null(typeof(ICacheEvictionPolicy).GetMethod("StartAsync"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue3_A_Discarded_QueryAsync_Releases_Its_Worker_When_The_Fetch_Settles()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var fetch = client.QueryAsync(QueryOptions.Create("prefetched", Handler).Build(), TestContext.Current.CancellationToken);
        Discard(fetch);
        Assert.True(client.FindQuery("prefetched")!.IsResolved);
        await fetch.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, WorkerCount(client));

        var observer = client.Subscribe(QueryOptions.Create("prefetched", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);
        Assert.Equal(1, WorkerCount(client));

        observer.Dispose();
        Assert.Equal(0, WorkerCount(client));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue3_An_Awaited_QueryAsync_Releases_Its_Worker_Too()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        await client.QueryAsync(QueryOptions.Create("fetched", Handler).Build(), TestContext.Current.CancellationToken);

        Assert.Equal(0, WorkerCount(client));
        Assert.NotNull(client.FindQuery("fetched"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue3_Subscribe_Path_Still_Cleans_Up()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var observer = client.Subscribe(QueryOptions.Create("subscribed", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);

        Assert.Equal(1, WorkerCount(client));

        observer.Dispose();
        Assert.Equal(0, WorkerCount(client));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue4_Concurrent_Callers_Share_One_Fetch_Instead_Of_Stampeding()
    {
        const int callers = 16;
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var handler = new GatedHandler<string>();
        var opts = QueryOptions.Create("stampede", handler.Handle).Build();

        var joined = new Counter();
        var allJoined = new Gate();
        var fetches = new Task<string>[callers];
        using var barrier = new Barrier(callers);
        var threads = Enumerable.Range(0, callers).Select(i => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            fetches[i] = client.QueryAsync(opts);
            if (joined.Increment() == callers) allJoined.Open();
        }, TaskCreationOptions.LongRunning)).ToArray();

        await allJoined.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(threads).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        handler.ReleaseAll("slow");
        var results = await Task.WhenAll(fetches).WaitAsync(TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal("slow", r));
        Assert.Equal(1, handler.Calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue5_OnSettled_Receives_Its_Own_Params_And_Data()
    {
        var handler = new GatedHandler<string>();
        var settlements = new Settlements();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
        var state = NewSettlingMutation(client, handler, settlements);

        _ = state.ExecuteAsync(new Box("SLOW"), TestContext.Current.CancellationToken);
        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        _ = state.ExecuteAsync(new Box("FAST"), TestContext.Current.CancellationToken);
        await handler.Started(1).WaitAsync(TestContext.Current.CancellationToken);

        handler.Release(1, "FAST");
        await settlements.For("FAST").Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("FAST", "FAST")], settlements.Observed());

        handler.Release(0, "SLOW");
        await settlements.For("SLOW").Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("SLOW", settlements.Observed().Single(x => x.Param == "SLOW").Data);
        Assert.Equal("FAST", settlements.Observed().Single(x => x.Param == "FAST").Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue5_OnSettled_Receives_Its_Own_Params_And_Data_When_The_First_Call_Settles_First()
    {
        var handler = new GatedHandler<string>();
        var settlements = new Settlements();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
        var state = NewSettlingMutation(client, handler, settlements);

        _ = state.ExecuteAsync(new Box("SLOW"), TestContext.Current.CancellationToken);
        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        _ = state.ExecuteAsync(new Box("FAST"), TestContext.Current.CancellationToken);
        await handler.Started(1).WaitAsync(TestContext.Current.CancellationToken);

        handler.Release(0, "SLOW");
        await settlements.For("SLOW").Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("SLOW", "SLOW")], settlements.Observed());

        handler.Release(1, "FAST");
        await settlements.For("FAST").Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("SLOW", settlements.Observed().Single(x => x.Param == "SLOW").Data);
        Assert.Equal("FAST", settlements.Observed().Single(x => x.Param == "FAST").Data);
    }

    private static RevalQuery.Core.Mutation.MutationState<Box, string> NewSettlingMutation(
        RevalClient client, GatedHandler<string> handler, Settlements settlements) =>
        client.CreateMutation(
            MutationOptions.Create<Box, string>(handler.Handle)
                .OnSettled(ctx =>
                {
                    settlements.Record(ctx.Result, ctx.Params.Name);
                    return Task.CompletedTask;
                })
                .Build(),
            () => { }).State;

    /// <summary>
    /// What each mutation's <c>OnSettled</c> saw, and a gate per param name that opens once it has run.
    /// </summary>
    private sealed class Settlements
    {
        private readonly ConcurrentDictionary<string, Gate> _gates = new();
        private readonly ConcurrentQueue<(string? Data, string Param)> _observed = new();

        public Gate For(string param) => _gates.GetOrAdd(param, _ => new Gate());

        public void Record(string? data, string param)
        {
            _observed.Enqueue((data, param));
            For(param).Open();
        }

        public (string? Data, string Param)[] Observed() => [.. _observed];
    }

    private sealed record Box(string Name);

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Issue6_FindQuery_Returns_Null_For_A_Mismatched_Result_Type()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var observer = client.Subscribe(QueryOptions.Create("typed", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);

        Assert.Null(client.FindQuery<int>("typed"));
        Assert.NotNull(client.FindQuery<string>("typed"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Issue7_Colliding_Hashes_Stay_Two_Distinct_Queries()
    {
        var (a, b) = FindCollidingKeys();

        Assert.Equal(
            QueryKeyComparer.Instance.GetHashCode(ValueTuple.Create(a)),
            QueryKeyComparer.Instance.GetHashCode(ValueTuple.Create(b)));
        Assert.False(QueryKeyComparer.Instance.Equals(ValueTuple.Create(a), ValueTuple.Create(b)));

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var first = client.Subscribe(QueryOptions.Create(a, static _ => Task.FromResult("A-DATA")).Build(), () => { });
        var second = client.Subscribe(QueryOptions.Create(b, static _ => Task.FromResult("B-DATA")).Build(), () => { });

        Assert.NotSame(first.Query, second.Query);
        Assert.Equal(a, ((RevalQuery.Core.Query.QueryState<ValueTuple<string>, string>)first.Query).Key.Item1);
        Assert.Equal(b, ((RevalQuery.Core.Query.QueryState<ValueTuple<string>, string>)second.Query).Key.Item1);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Issue7_Segments_Of_Different_Types_Are_Different_Queries()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        client.Subscribe(QueryOptions.Create(("users", 1), static _ => Task.FromResult("int")).Build(), () => { });
        client.Subscribe(QueryOptions.Create(("users", "1"), static _ => Task.FromResult("string")).Build(), () => { });

        Assert.NotSame(client.FindQuery(("users", 1)), client.FindQuery(("users", "1")));
        Assert.NotNull(client.FindQuery(("users", 1)));
        Assert.NotNull(client.FindQuery(("users", "1")));
    }

    private static (string A, string B) FindCollidingKeys()
    {
        var seen = new Dictionary<int, string>();

        for (var i = 0; i < 4_000_000; i++)
        {
            var key = "collide-" + i;
            var hash = QueryKeyComparer.Instance.GetHashCode(ValueTuple.Create(key));
            if (seen.TryGetValue(hash, out var prior)) return (prior, key);
            seen[hash] = key;
        }

        throw new InvalidOperationException("no collision found in 4,000,000 keys");
    }

    private static int EvictionSubscriberCount(TtlQueryGarbageCollector gc) => gc.EvictionSubscriberCount;
}

/// <summary>
/// Probabilistic regression net for the registry race the deterministic tests cannot force: it
/// happens inside library code with no callback in the window.
/// </summary>
[Collection("stress")]
public class IssueReproductionStressTests
{
    // Probabilistic regression net: can find a registry race, cannot prove its absence.
    [Fact(Explicit = true, Timeout = HangGuardMilliseconds)]
    public async Task Issue4_Concurrent_QueryAsync_Keeps_The_Registry_Intact()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var lostEntries = 0;

        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var client = new RevalClient(sp, new RevalQueryOptions());
            var key = $"pf{attempt}";
            var opts = QueryOptions.Create(key, SpinHandler).Build();

            using var barrier = new Barrier(16);
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(async () =>
            {
                barrier.SignalAndWait();
                try { await client.QueryAsync(opts); }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            }, TaskCreationOptions.LongRunning).Unwrap()).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TestContext.Current.CancellationToken);

            if (client.FindQuery(key) is null) Interlocked.Increment(ref lostEntries);
        }

        Assert.Empty(failures);
        Assert.Equal(0, lostEntries);
    }

    private static Task<string> SpinHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        Thread.SpinWait(300);
        return Task.FromResult("x");
    }
}
