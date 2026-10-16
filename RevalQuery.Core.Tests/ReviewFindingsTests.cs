using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Tests;

public class ReviewFindingsTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_Observer_That_Throws_Does_Not_Wedge_The_Query()
    {
        using var client = NewClient();
        var calls = new Counter();

        var options = QueryOptions.Create("wedged", _ =>
        {
            calls.Increment();
            return Task.FromResult("ok");
        }).Build();

        using var observer = client.Subscribe(options, () => throw new ObjectDisposedException("circuit"));

        Assert.Equal(1, calls.Value);
        Assert.True(observer.Query.IsIdle);

        client.Invalidate("wedged");

        Assert.Equal(2, calls.Value);
        Assert.True(observer.Query.IsIdle);
        Assert.True(observer.Query.IsResolved);
    }

    // The invariant in the name is held by RevalClient.RunAndReleaseAsync, which retries on
    // FetchOutcome.NotRun when the worker is released between PrepareRun and RunAsync. No user
    // code runs in that window, so no test through the client can open it. This covers the
    // worker half: a released worker runs nothing and reports NotRun. The client-level retry is
    // a documented limitation, not tested.
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Released_Worker_Reports_NotRun_Rather_Than_Data_It_Never_Fetched()
    {
        var calls = new Counter();
        var state = new QueryState<ValueTuple<string>, string>(
            ValueTuple.Create("released"),
            _ =>
            {
                calls.Increment();
                return Task.FromResult("value");
            },
            null, null, null);
        var worker = new QueryWorker<ValueTuple<string>, string>(new RevalQueryOptions { TimeProvider = _clock }, new ServiceCollection().BuildServiceProvider(), state);

        worker.Dispose();

        Assert.Equal(FetchOutcome.NotRun, await worker.RunAsync().WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls.Value);
        Assert.Null(state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Second_QueryAsync_Joins_The_Fetch_Already_Running()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("released", handler.Handle).Build();

        var fetch = client.QueryAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        var second = client.QueryAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        handler.Release(0, "value");

        Assert.Equal("value", await fetch.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("value", await second.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Resubscribing_While_The_Previous_Observer_Leaves_Keeps_Polling()
    {
        var interval = TimeSpan.FromSeconds(10);
        using var client = NewClient();
        var calls = new Counter();

        var options = QueryOptions.Create("polled", _ => Task.FromResult($"ok-{calls.Increment()}"))
            .ConfigureFetch(f => f.RefetchInterval(interval)).Build();

        for (var round = 0; round < 20; round++)
        {
            var first = client.Subscribe(options, () => { });
            var second = client.Subscribe(options, () => { });
            first.Dispose();
            second.Dispose();
        }

        using var observer = client.Subscribe(options, () => { });
        var before = calls.Value;

        _clock.Advance(interval);
        _clock.Advance(interval);
        _clock.Advance(interval);

        Assert.Equal(before + 3, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Stale_Persisted_Data_Still_Refetches()
    {
        var storedAt = _clock.GetUtcNow().AddHours(-3);
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var handler = new GatedHandler<string>();

        var options = QueryOptions.Create("stale", handler.Handle)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build();

        using var observer = client.Subscribe(options, () => { });
        var state = client.FindQuery<string>("stale")!;

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(storedAt)));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("from-disk", state.Data);
        Assert.Equal(storedAt, state.LastUpdatedAt);
        Assert.True(state.IsFetching);
        Assert.True(handler.Started(0).IsCompleted);

        handler.Release(0, "from-network");
        await Signals.UntilAsync(state, s => s.IsIdle && s.Data == "from-network", TestContext.Current.CancellationToken);

        Assert.True(state.LastUpdatedAt > storedAt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

        Assert.NotEmpty(evicted);
        policy.Dispose();
    }

    private RevalClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, persistence: persistence);
}
