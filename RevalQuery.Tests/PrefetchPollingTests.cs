using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class PrefetchPollingTests
{
    private const string Key = "poll";

    private readonly QueryClient _client;

    public PrefetchPollingTests()
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
    public async Task PrefetchThenSubscribe_InitialDataFromCache()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        _client.PrefetchQuery(queryOptions);
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
    public async Task PrefetchDoesNotStartPolling()
    {
        var queryOptions = QueryOptions.Create(Key, UniqueHandler)
            .ConfigureFetch(b => b.RefetchInterval(TimeSpan.FromMilliseconds(50)))
            .Build();

        _client.PrefetchQuery(queryOptions);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var firstData = ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!).Data;

        await Task.Delay(200);

        var state = _client.GetOrCreateQuery(queryOptions);
        Assert.Equal(firstData, state.Data);
    }

    private static Task<string> UniqueHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult($"data-{DateTime.UtcNow.Ticks}");

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("cached");
}