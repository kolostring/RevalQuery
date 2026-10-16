using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Issue2_BackgroundEviction_Runs_Without_A_Manual_Start()
    {
        var sp = new ServiceCollection()
            .AddRevalQuery(o => o.CacheOptions = new CoreCacheOptions(
                GcTime: TimeSpan.Zero,
                GcInterval: TimeSpan.FromMilliseconds(20)))
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        var gc = (TtlQueryGarbageCollector)scope.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>();
        var evicted = new Gate();
        gc.OnEvictionRequired += key =>
        {
            if (QueryKeyComparer.Instance.Equals(key, ValueTuple.Create("gc-key"))) evicted.Open();
        };

        var observer = client.Subscribe(QueryOptions.Create("gc-key", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);
        observer.Dispose();

        await evicted.Task.WaitAsync(HangGuard);

        Assert.Null(client.FindQuery("gc-key"));
    }

    [Fact]
    public void Issue2_EvictionPolicy_Needs_No_Host_To_Start()
    {
        var services = new ServiceCollection().AddRevalQuery();

        Assert.DoesNotContain(services, d => d.ServiceType.Name.Contains("IHostedService"));
        Assert.Null(typeof(ICacheEvictionPolicy).GetMethod("StartAsync"));
    }

    [Fact]
    public async Task Issue3_A_Discarded_QueryAsync_Releases_Its_Worker_When_The_Fetch_Settles()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var fetch = client.QueryAsync(QueryOptions.Create("prefetched", Handler).Build());
        Discard(fetch);
        Assert.True(client.FindQuery("prefetched")!.IsResolved);
        await fetch.WaitAsync(HangGuard);

        Assert.Equal(0, WorkerCount(client));

        var observer = client.Subscribe(QueryOptions.Create("prefetched", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);
        Assert.Equal(1, WorkerCount(client));

        observer.Dispose();
        Assert.Equal(0, WorkerCount(client));
    }

    [Fact]
    public async Task Issue3_An_Awaited_QueryAsync_Releases_Its_Worker_Too()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        await client.QueryAsync(QueryOptions.Create("fetched", Handler).Build());

        Assert.Equal(0, WorkerCount(client));
        Assert.NotNull(client.FindQuery("fetched"));
    }

    [Fact]
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

    [Fact]
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

        await allJoined.Task.WaitAsync(HangGuard);
        await Task.WhenAll(threads).WaitAsync(HangGuard);
        Assert.Equal(1, handler.Calls);

        handler.ReleaseAll("slow");
        var results = await Task.WhenAll(fetches).WaitAsync(HangGuard);

        Assert.All(results, r => Assert.Equal("slow", r));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Issue5_OnSettled_Receives_Its_Own_Params_And_Data()
    {
        var run = new MutationRun();

        run.Start("SLOW");
        await run.Started("SLOW").WaitAsync(HangGuard);
        run.Start("FAST");
        await run.Started("FAST").WaitAsync(HangGuard);

        run.Release("FAST");
        await run.Settled("FAST").WaitAsync(HangGuard);
        Assert.Equal([("FAST", "FAST")], run.Observed());

        run.Release("SLOW");
        await run.Settled("SLOW").WaitAsync(HangGuard);

        Assert.Equal("SLOW", run.Observed().Single(x => x.Param == "SLOW").Data);
        Assert.Equal("FAST", run.Observed().Single(x => x.Param == "FAST").Data);
    }

    [Fact]
    public async Task Issue5_OnSettled_Receives_Its_Own_Params_And_Data_When_The_First_Call_Settles_First()
    {
        var run = new MutationRun();

        run.Start("SLOW");
        await run.Started("SLOW").WaitAsync(HangGuard);
        run.Start("FAST");
        await run.Started("FAST").WaitAsync(HangGuard);

        run.Release("SLOW");
        await run.Settled("SLOW").WaitAsync(HangGuard);
        Assert.Equal([("SLOW", "SLOW")], run.Observed());

        run.Release("FAST");
        await run.Settled("FAST").WaitAsync(HangGuard);

        Assert.Equal("SLOW", run.Observed().Single(x => x.Param == "SLOW").Data);
        Assert.Equal("FAST", run.Observed().Single(x => x.Param == "FAST").Data);
    }

    /// <summary>
    /// Mutation calls keyed by name, each held until the test releases it and each reporting
    /// when its <c>OnSettled</c> has run.
    /// </summary>
    private sealed class MutationRun
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Gate> _started = [];
        private readonly Dictionary<string, Gate> _settled = [];
        private readonly Dictionary<string, TaskCompletionSource<string>> _release = [];
        private readonly List<(string? Data, string Param)> _observed = [];
        private readonly RevalClient _client;
        private readonly RevalQuery.Core.Mutation.MutationState<Box, string> _state;

        public MutationRun()
        {
            _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
            var options = MutationOptions.Create<Box, string>(async ctx =>
                {
                    Gate(_started, ctx.Params.Name).Open();
                    return await Gated(ctx.Params.Name).Task;
                })
                .OnSettled((data, _, p) =>
                {
                    lock (_lock) _observed.Add((data, p.Name));
                    Gate(_settled, p.Name).Open();
                    return Task.CompletedTask;
                });
            _state = _client.CreateMutation(options.Build(), () => { }).State;
        }

        public void Start(string name) => _ = _state.ExecuteAsync(new Box(name));

        public Task Started(string name) => Gate(_started, name).Task;

        public Task Settled(string name) => Gate(_settled, name).Task;

        public void Release(string name) => Gated(name).TrySetResult(name);

        public (string? Data, string Param)[] Observed()
        {
            lock (_lock) return [.. _observed];
        }

        private Gate Gate(Dictionary<string, Gate> gates, string name)
        {
            lock (_lock)
            {
                if (!gates.TryGetValue(name, out var gate)) gates[name] = gate = new Gate();
                return gate;
            }
        }

        private TaskCompletionSource<string> Gated(string name)
        {
            lock (_lock)
            {
                if (!_release.TryGetValue(name, out var tcs))
                    _release[name] = tcs = new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                return tcs;
            }
        }
    }

    private sealed record Box(string Name);

    [Fact]
    public async Task Issue6_FindQuery_Returns_Null_For_A_Mismatched_Result_Type()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var observer = client.Subscribe(QueryOptions.Create("typed", Handler).Build(), () => { });
        Assert.True(observer.Query.IsResolved);

        Assert.Null(client.FindQuery<int>("typed"));
        Assert.NotNull(client.FindQuery<string>("typed"));
    }

    [Fact]
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

    [Fact]
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

    private static int EvictionSubscriberCount(TtlQueryGarbageCollector gc)
    {
        var field = typeof(TtlQueryGarbageCollector)
            .GetField("OnEvictionRequired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((Delegate?)field.GetValue(gc))?.GetInvocationList().Length ?? 0;
    }
}

/// <summary>
/// Probabilistic regression net for the registry race the deterministic tests cannot force: it
/// happens inside library code with no callback in the window.
/// </summary>
[Collection("stress")]
public class IssueReproductionStressTests
{
    // Probabilistic regression net: can find a registry race, cannot prove its absence.
    [Fact]
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
            await Task.WhenAll(tasks).WaitAsync(HangGuard);

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
