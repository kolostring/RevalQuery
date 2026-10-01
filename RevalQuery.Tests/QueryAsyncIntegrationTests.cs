using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class QueryAsyncIntegrationTests
{
    private const string Key = "prefetch";

    private readonly QueryClient _client;

    public QueryAsyncIntegrationTests()
    {
        _client = new QueryClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    [Fact]
    public async Task SubscribeAfterDiscardedQueryAsync_UsesCachedData()
    {
        var queryOptions = QueryOptions.Create(Key, CachedHandler).Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var observer = _client.Subscribe(queryOptions, () => { });

        Assert.Equal("cached", observer.Query.Data);
        Assert.True(observer.Query.IsResolved);
    }

    [Fact]
    public async Task SubscribeAfterQueryAsync_UsesCachedData()
    {
        var queryOptions = QueryOptions.Create(Key, FetchedHandler).Build();

        await _client.QueryAsync(queryOptions);
        var observer = _client.Subscribe(queryOptions, () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        Assert.Equal("fetched", observer.Query.Data);
        Assert.True(observer.Query.IsResolved);
    }

    [Fact]
    public async Task InvalidateAfterQueryAsync_TriggersRefetch()
    {
        var queryOptions = QueryOptions.Create(Key, StaticDataHandler)
            .ConfigureFetch(b => b.StaleTime(TimeSpan.FromSeconds(30)))
            .Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var observer = _client.Subscribe(queryOptions, () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
        var initialData = observer.Query.Data;
        Assert.NotNull(initialData);

        _client.Invalidate(Key);
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        Assert.NotSame(initialData, observer.Query.Data);
    }

    [Fact]
    public async Task QueryAsync_WithEnabledFalse_StillFetches()
    {
        var queryOptions = QueryOptions.Create(Key, DataHandler)
            .Enabled(false)
            .Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var state = _client.FindQuery(Key);
        Assert.NotNull(state);
        Assert.True(state.IsResolved);
    }

    private static Task<string> StaticDataHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult($"v{DateTime.UtcNow.Ticks}");

    private static Task<string> CachedHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("cached");

    private static Task<string> FetchedHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("fetched");

    private static Task<string> DataHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");
}