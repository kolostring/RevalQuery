using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class PollingTests
{
    private const string Key = "poll";

    private readonly QueryClient _client;

    public PollingTests()
    {
        _client = new QueryClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    [Fact]
    public async Task SubscribeWithRefetchInterval_Refetches()
    {
        var queryOptions = QueryOptions.Create(Key, UniqueHandler)
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(50)))
            .Build();

        var observer = _client.Subscribe(queryOptions, () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        var firstData = observer.Query.Data;

        await TestUtils.WaitForStateAsync(observer.Query, _ => observer.Query.Data != firstData, 2000);

        Assert.NotSame(firstData, observer.Query.Data);
    }

    [Fact]
    public async Task DiscardedQueryAsyncThenSubscribe_InitialDataFromCache()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        var state = _client.GetOrCreateQuery(queryOptions);
        var firstData = state.Data;
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var observer = _client.Subscribe(queryOptions, () => { });

        Assert.Equal(firstData, observer.Query.Data);
    }

    [Fact]
    public async Task SubscribeThenDispose_PollingStops()
    {
        var queryOptions = QueryOptions.Create(Key, UniqueHandler)
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(30)))
            .Build();

        var observer = _client.Subscribe(queryOptions, () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        var dataBeforeDispose = observer.Query.Data;

        observer.Dispose();

        var state = _client.GetOrCreateQuery(queryOptions);
        Assert.Equal(dataBeforeDispose, state.Data);
    }

    [Fact]
    public async Task AQueryNobodyObservesDoesNotPoll()
    {
        var queryOptions = QueryOptions.Create(Key, UniqueHandler)
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(50)))
            .Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var firstData = ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!).Data;

        await Task.Delay(200);

        var state = _client.GetOrCreateQuery(queryOptions);
        Assert.Equal(firstData, state.Data);
    }

    [Fact]
    public async Task SubscribingEnabledToAQueryOnlyDisabledObserversHold_StartsPolling()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key, ctx =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("data");
            })
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(40)))
            .Build();

        using var disabled = _client.Subscribe(queryOptions with { Enabled = false }, () => { });
        using var enabled = _client.Subscribe(queryOptions, () => { });

        // One fetch for the subscription, then the loop it has to start.
        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref calls) >= 3, 2000);

        Assert.True(Volatile.Read(ref calls) >= 3);
    }

    private static Task<string> UniqueHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult($"data-{DateTime.UtcNow.Ticks}");

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("cached");
}