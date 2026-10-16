using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Tests;

public class PollingTests : IDisposable
{
    private const string Key = "poll";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new();
    private readonly RevalClient _client;

    public PollingTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock });
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public void SubscribeWithRefetchInterval_Refetches()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(Interval))
            .Build();

        using var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal("data-1", observer.Query.Data);

        _clock.Advance(Interval);

        Assert.Equal("data-2", observer.Query.Data);
        Assert.Equal(2, calls.Value);
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
    public void SubscribeThenDispose_PollingStops()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(Interval))
            .Build();

        var observer = _client.Subscribe(queryOptions, () => { });
        _clock.Advance(Interval);
        Assert.Equal(2, calls.Value);

        observer.Dispose();
        _clock.Advance(Interval * 3);

        Assert.Equal(2, calls.Value);
        Assert.Equal(0, WorkerCount(_client));
    }

    [Fact]
    public async Task AQueryNobodyObservesDoesNotPoll()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(Interval))
            .Build();

        var fetch = _client.QueryAsync(queryOptions);
        Assert.Equal("data-1", await fetch.WaitAsync(HangGuard));
        _clock.Advance(Interval * 3);

        Assert.Equal(1, calls.Value);
    }

    [Fact]
    public void SubscribingEnabledToAQueryOnlyDisabledObserversHold_StartsPolling()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, Counting(calls))
            .ConfigureFetch(b => b.RefetchInterval(Interval))
            .Build();

        using var disabled = _client.Subscribe(queryOptions with { Enabled = false }, () => { });
        _clock.Advance(Interval);
        Assert.Equal(0, calls.Value);

        using var enabled = _client.Subscribe(queryOptions, () => { });
        Assert.Equal(1, calls.Value);

        _clock.Advance(Interval);
        Assert.Equal(2, calls.Value);
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
