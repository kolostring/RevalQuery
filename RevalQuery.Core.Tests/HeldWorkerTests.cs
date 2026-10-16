using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

/// <summary>
/// A worker stays alive while a run is claimed or a fetch is in flight, so neither a release
/// nor a garbage-collection eviction can take it from a caller that is awaiting it.
/// </summary>
public class HeldWorkerTests
{
    private static RevalClient NewClient(ManualEviction eviction) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

    [Fact]
    public async Task An_Eviction_During_The_Fetch_Neither_Cancels_It_Nor_Loses_The_Data()
    {
        var eviction = new ManualEviction();
        using var client = NewClient(eviction);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create<string>("held", handler.Handle).Build();

        var fetch = client.QueryAsync(options);
        await handler.Started(0).WaitAsync(HangGuard);

        eviction.Evict(options.Key);

        Assert.False(handler.Token(0).IsCancellationRequested);

        handler.Release(0, "data");

        Assert.Equal("data", await fetch.WaitAsync(HangGuard));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task An_Eviction_Refused_During_The_Fetch_Registers_The_Query_Again_When_It_Ends()
    {
        var eviction = new ManualEviction();
        using var client = NewClient(eviction);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create<string>("held", handler.Handle).Build();

        var fetch = client.QueryAsync(options);
        await handler.Started(0).WaitAsync(HangGuard);
        eviction.Evict(options.Key);
        var before = eviction.Registered.Count;

        handler.Release(0, "data");
        await fetch.WaitAsync(HangGuard);

        Assert.True(eviction.Registered.Count > before);
        Assert.Contains(eviction.Registered, key => key.Equals(options.Key));
    }

    [Fact]
    public async Task A_Claimed_Worker_Refuses_Release_Until_The_Claim_Becomes_A_Run_That_Ends()
    {
        var handler = new GatedHandler<string>();
        var (worker, state) = NewWorker(handler);
        var released = new Counter();
        worker.OnReleaseDue += () => released.Increment();

        worker.Claim();

        Assert.False(worker.TryRelease());

        var run = worker.RunClaimedAsync();
        await handler.Started(0).WaitAsync(HangGuard);
        handler.Release(0, "data");

        Assert.Equal(FetchOutcome.Settled, await run.WaitAsync(HangGuard));
        Assert.Equal("data", state.Data);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, released.Value);
    }

    [Fact]
    public void A_Dropped_Claim_Lets_A_Refused_Release_Through()
    {
        var (worker, _) = NewWorker(new GatedHandler<string>());
        var released = new Counter();
        worker.OnReleaseDue += () => released.Increment();

        worker.Claim();
        Assert.False(worker.TryRelease());
        Assert.Equal(0, released.Value);

        worker.Unclaim();

        Assert.Equal(1, released.Value);
        Assert.True(worker.TryRelease());
    }

    [Fact]
    public void Two_Claims_Keep_The_Worker_Until_Both_Are_Gone()
    {
        var (worker, _) = NewWorker(new GatedHandler<string>());
        var released = new Counter();
        worker.OnReleaseDue += () => released.Increment();

        worker.Claim();
        worker.Claim();
        Assert.False(worker.TryRelease());

        worker.Unclaim();
        Assert.Equal(0, released.Value);

        worker.Unclaim();
        Assert.Equal(1, released.Value);
    }

    private static (QueryWorker<ValueTuple<string>, string> Worker, QueryState<ValueTuple<string>, string> State) NewWorker(
        GatedHandler<string> handler)
    {
        var options = QueryOptions.Create<string>("claimed", handler.Handle).Build();
        var state = new QueryState<ValueTuple<string>, string>(
            options.Key, options.Handler, null, null, null);
        return (new QueryWorker<ValueTuple<string>, string>(
            new RevalQueryOptions(), new ServiceCollection().BuildServiceProvider(), state), state);
    }
}
