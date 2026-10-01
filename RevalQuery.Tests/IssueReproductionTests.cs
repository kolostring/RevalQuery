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

namespace RevalQuery.Tests;

/// <summary>
/// The specification for the 0.4.0 correctness release. Each test names the defect it
/// covers and asserts the fixed behaviour; every one of them failed before the fix.
/// </summary>
public class IssueReproductionTests
{
    private static Task<string> Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("result");

    // ---------- Issue 1: DI lifetimes ----------

    [Fact]
    public void Issue1_EvictionPolicy_Is_Scoped_Alongside_QueryClient()
    {
        var sp = new ServiceCollection().AddRevalQuery().BuildServiceProvider();

        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var c1 = scope1.ServiceProvider.GetRequiredService<QueryClient>();
        var c2 = scope2.ServiceProvider.GetRequiredService<QueryClient>();

        Assert.NotSame(c1, c2);
        Assert.NotSame(
            scope1.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>(),
            scope2.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>());

        var services = new ServiceCollection().AddRevalQuery();
        Assert.All(
            services.Where(d => d.ServiceType == typeof(ICacheEvictionPolicy) || d.ServiceType == typeof(QueryClient)),
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
        var c1 = scope1.ServiceProvider.GetRequiredService<QueryClient>();
        var c2 = scope2.ServiceProvider.GetRequiredService<QueryClient>();
        var gc1 = (TtlQueryGarbageCollector)scope1.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>();

        var opts = QueryOptions.Create("shared-key", Handler).Build();

        var o1 = c1.Subscribe(opts, () => { });
        var o2 = c2.Subscribe(opts, () => { });
        await TestUtils.WaitForStateAsync(o1.Query, s => s.IsResolved);
        await TestUtils.WaitForStateAsync(o2.Query, s => s.IsResolved);

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
            var client = scope.ServiceProvider.GetRequiredService<QueryClient>();
            var gc = (TtlQueryGarbageCollector)scope.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>();

            var observer = client.Subscribe(QueryOptions.Create($"k{i}", Handler).Build(), () => { });
            await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
            observer.Dispose();

            Assert.Equal(1, EvictionSubscriberCount(gc));

            scope.Dispose();

            Assert.Equal(0, EvictionSubscriberCount(gc));
        }
    }

    // ---------- Issue 2: the eviction policy runs without anything starting it ----------

    [Fact]
    public async Task Issue2_BackgroundEviction_Runs_Without_A_Manual_Start()
    {
        var sp = new ServiceCollection()
            .AddRevalQuery(o => o.CacheOptions = new CoreCacheOptions(
                GcTime: TimeSpan.Zero,
                GcInterval: TimeSpan.FromMilliseconds(20)))
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<QueryClient>();

        var observer = client.Subscribe(QueryOptions.Create("gc-key", Handler).Build(), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
        observer.Dispose();

        await WaitUntil(() => client.FindQuery("gc-key") is null, 2000);

        Assert.Null(client.FindQuery("gc-key"));
    }

    [Fact]
    public void Issue2_EvictionPolicy_Needs_No_Host_To_Start()
    {
        var services = new ServiceCollection().AddRevalQuery();

        // Nothing to start means nothing to register: WebAssembly has no host to run one.
        Assert.DoesNotContain(services, d => d.ServiceType.Name.Contains("IHostedService"));
        Assert.Null(typeof(ICacheEvictionPolicy).GetMethod("StartAsync"));
    }

    // ---------- Issue 3: worker lifecycle ----------

    [Fact]
    public async Task Issue3_A_Discarded_QueryAsync_Releases_Its_Worker_When_The_Fetch_Settles()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        TestUtils.Discard(client.QueryAsync(QueryOptions.Create("prefetched", Handler).Build()));
        await WaitUntil(() => client.FindQuery("prefetched")?.IsResolved == true);
        await WaitUntil(() => WorkerCount(client) == 0);

        Assert.Equal(0, WorkerCount(client));

        var observer = client.Subscribe(QueryOptions.Create("prefetched", Handler).Build(), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
        Assert.Equal(1, WorkerCount(client));

        observer.Dispose();
        Assert.Equal(0, WorkerCount(client));
    }

    [Fact]
    public async Task Issue3_An_Awaited_QueryAsync_Releases_Its_Worker_Too()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        await client.QueryAsync(QueryOptions.Create("fetched", Handler).Build());

        Assert.Equal(0, WorkerCount(client));
        Assert.NotNull(client.FindQuery("fetched"));
    }

    [Fact]
    public async Task Issue3_Subscribe_Path_Still_Cleans_Up()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        var observer = client.Subscribe(QueryOptions.Create("subscribed", Handler).Build(), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        Assert.Equal(1, WorkerCount(client));

        observer.Dispose();
        Assert.Equal(0, WorkerCount(client));
    }

    // ---------- Issue 4: QueryClient is callable from any thread ----------

    [Fact]
    public async Task Issue4_Concurrent_QueryAsync_Keeps_The_Registry_Intact()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var lostEntries = 0;

        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var client = new QueryClient(sp, new RevalQueryOptions());
            var key = $"pf{attempt}";
            var opts = QueryOptions.Create(key, SpinHandler).Build();

            using var barrier = new Barrier(16);
            // Awaited rather than discarded. QueryAsync reports everything through its task,
            // so a discard here would hand the failures bag nothing to collect and the
            // assertion below would pass on an empty set whatever the registry did.
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try { await client.QueryAsync(opts); }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            })).ToArray();
            await Task.WhenAll(tasks);
            await Task.Delay(2);

            if (client.FindQuery(key) is null) Interlocked.Increment(ref lostEntries);
        }

        Assert.Empty(failures);
        Assert.Equal(0, lostEntries);
    }

    [Fact]
    public async Task Issue4_Concurrent_Callers_Share_One_Fetch_Instead_Of_Stampeding()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        SlowHandlerCalls = 0;
        var opts = QueryOptions.Create("stampede", SlowHandler).Build();

        using var barrier = new Barrier(16);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return client.QueryAsync(opts);
        })));

        Assert.All(results, r => Assert.Equal("slow", r));
        Assert.Equal(1, SlowHandlerCalls);
    }

    private static int SlowHandlerCalls;

    private static async Task<string> SlowHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        Interlocked.Increment(ref SlowHandlerCalls);
        await Task.Delay(150, ctx.CancellationToken ?? CancellationToken.None);
        return "slow";
    }

    private static Task<string> SpinHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        Thread.SpinWait(300);
        return Task.FromResult("x");
    }

    // ---------- Issue 5: a mutation's OnSettled sees its own result ----------

    [Fact]
    public async Task Issue5_OnSettled_Receives_Its_Own_Params_And_Data()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var observed = new List<(string? data, string param)>();
        var gate = new object();

        var options = MutationOptions.Create<Box, string>(static async ctx =>
            {
                await Task.Delay(ctx.Params.DelayMs, CancellationToken.None);
                return ctx.Params.Name;
            })
            .OnSettled((data, _, p) =>
            {
                lock (gate) observed.Add((data, p.Name));
                return Task.CompletedTask;
            });

        var state = new RevalQuery.Core.Mutation.MutationState<Box, string>(options.Build(), sp);

        var slow = state.ExecuteAsync(new Box("SLOW", 300));
        await Task.Delay(50);
        var fast = state.ExecuteAsync(new Box("FAST", 10));
        await Task.WhenAll(slow, fast);

        Assert.Equal("SLOW", observed.Single(x => x.param == "SLOW").data);
        Assert.Equal("FAST", observed.Single(x => x.param == "FAST").data);
    }

    private sealed record Box(string Name, int DelayMs);

    // ---------- Issue 6: FindQuery returns null rather than throwing ----------

    [Fact]
    public async Task Issue6_FindQuery_Returns_Null_For_A_Mismatched_Result_Type()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        var observer = client.Subscribe(QueryOptions.Create("typed", Handler).Build(), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        Assert.Null(client.FindQuery<int>("typed"));
        Assert.NotNull(client.FindQuery<string>("typed"));
    }

    // ---------- Issue 7: keys are compared by value, so a hash collision aliases nothing ----------

    [Fact]
    public void Issue7_Colliding_Hashes_Stay_Two_Distinct_Queries()
    {
        var (a, b) = FindCollidingKeys();

        Assert.Equal(
            QueryKeyComparer.Instance.GetHashCode(ValueTuple.Create(a)),
            QueryKeyComparer.Instance.GetHashCode(ValueTuple.Create(b)));
        Assert.False(QueryKeyComparer.Instance.Equals(ValueTuple.Create(a), ValueTuple.Create(b)));

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

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
        using var client = new QueryClient(sp, new RevalQueryOptions());

        client.Subscribe(QueryOptions.Create(("users", 1), static _ => Task.FromResult("int")).Build(), () => { });
        client.Subscribe(QueryOptions.Create(("users", "1"), static _ => Task.FromResult("string")).Build(), () => { });

        Assert.NotSame(client.FindQuery(("users", 1)), client.FindQuery(("users", "1")));
        Assert.NotNull(client.FindQuery(("users", 1)));
        Assert.NotNull(client.FindQuery(("users", "1")));
    }

    /// <summary>
    /// Finds two distinct string keys whose 32-bit hashes collide in this process.
    /// </summary>
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

    // ---------- helpers ----------

    private static int WorkerCount(QueryClient client)
    {
        var registry = (QueryRegistry)typeof(QueryClient)
            .GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;

        return QueryRegistry.WorkersFrom(registry.Root).Count;
    }

    private static int EvictionSubscriberCount(TtlQueryGarbageCollector gc)
    {
        var field = typeof(TtlQueryGarbageCollector)
            .GetField("OnEvictionRequired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((Delegate?)field.GetValue(gc))?.GetInvocationList().Length ?? 0;
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }
}
