using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Tests;

/// <remarks>
/// Polling runs on the library's real timer until the library takes a <c>TimeProvider</c>. Until
/// then these tests wait on signals for the polls they need and prove the absence of polling
/// structurally, through the worker that would run it, rather than by waiting it out.
/// </remarks>
public class PollingTests : IDisposable
{
    private const string Key = "poll";

    private readonly RevalClient _client;

    public PollingTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task SubscribeWithRefetchInterval_Refetches()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(50)))
            .Build();
        var polled = Signals.UntilAsync(_client.GetOrCreateQuery(queryOptions), q => q.Data == "data-2");

        using var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal("data-1", observer.Query.Data);

        await polled;
        // The poll may already have moved on, so only the first fetch's data is ruled out.
        Assert.NotEqual("data-1", observer.Query.Data);
        Assert.True(calls.Value >= 2);
    }

    [Fact]
    public async Task DiscardedQueryAsyncThenSubscribe_InitialDataFromCache()
    {
        var handler = new GatedHandler<string>();
        handler.Release(0, "cached");
        var queryOptions = QueryOptions.Create(Key, handler.Handle).Build();

        var fetch = _client.QueryAsync(queryOptions);
        Discard(fetch);
        await fetch.WaitAsync(HangGuard);

        // Relies on the clock having ticked since the fetch: StaleTime zero counts an age of
        // exactly zero as fresh (L1), and then the subscriber would not refetch.
        using var observer = _client.Subscribe(queryOptions, () => { });

        Assert.Equal("cached", observer.Query.Data);
        Assert.True(observer.Query.IsFetching);
        Assert.Equal(2, handler.Calls);

        var refreshed = Signals.UntilAsync(observer.Query, q => q.Data == "fresh");
        handler.Release(1, "fresh");
        await refreshed;
    }

    [Fact]
    public async Task SubscribeThenDispose_PollingStops()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(30)))
            .Build();
        var polled = Signals.UntilAsync(_client.GetOrCreateQuery(queryOptions), q => q.Data == "data-2");

        var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal("data-1", observer.Query.Data);
        await polled;

        observer.Dispose();

        // Structural until the poll timer can be advanced by hand: the worker that polls is
        // gone, and a disposed worker neither polls nor fetches.
        Assert.Equal(0, WorkerCount(_client));
    }

    [Fact]
    public async Task AQueryNobodyObservesDoesNotPoll()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(50)))
            .Build();

        var fetch = _client.QueryAsync(queryOptions);
        Assert.Equal("data-1", await fetch.WaitAsync(HangGuard));

        // Structural until the poll timer can be advanced by hand: with no observer no worker
        // remains to poll.
        Assert.Equal(1, calls.Value);
        Assert.Equal(0, WorkerCount(_client));
    }

    [Fact]
    public async Task SubscribingEnabledToAQueryOnlyDisabledObserversHold_StartsPolling()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle)
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(40)))
            .Build();

        using var disabled = _client.Subscribe(queryOptions with { Enabled = false }, () => { });
        Assert.Equal(0, handler.Calls);

        using var enabled = _client.Subscribe(queryOptions, () => { });
        Assert.Equal(1, handler.Calls);
        handler.Release(0, "data");

        await handler.Started(1).WaitAsync(HangGuard);
        handler.Release(1, "data");
        await handler.Started(2).WaitAsync(HangGuard);
    }

    private static Func<QueryHandlerExecutionContext<ValueTuple<string>>, Task<string>> Counting(Counter calls) =>
        _ => Task.FromResult($"data-{calls.Increment()}");

    private static int WorkerCount(RevalClient client)
    {
        var gate = typeof(RevalClient).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        var registry = (QueryRegistry)typeof(RevalClient)
            .GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;

        lock (gate) return QueryRegistry.WorkersFrom(registry.Root).Count;
    }
}
