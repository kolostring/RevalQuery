using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class QueryAsyncIntegrationTests : IDisposable
{
    private const string Key = "prefetch";

    private readonly RevalClient _client;

    public QueryAsyncIntegrationTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    public void Dispose() => _client.Dispose();

    /// <remarks>
    /// Any refetch the subscription starts is parked at the handler's gate, so the data seen
    /// straight after subscribing can only have come from the cache.
    /// </remarks>
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task SubscribeAfterDiscardedQueryAsync_UsesCachedData()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle).Build();

        Discard(_client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));
        var state = _client.FindQuery<string>(Key)!;
        var resolved = Signals.UntilAsync(state, s => s.IsResolved, TestContext.Current.CancellationToken);
        handler.Release(0, "cached");
        await resolved;

        var observer = _client.Subscribe(queryOptions, () => { });

        Assert.Equal("cached", observer.Query.Data);
        Assert.True(observer.Query.IsResolved);
    }

    /// <remarks>
    /// As <see cref="SubscribeAfterDiscardedQueryAsync_UsesCachedData"/>, with the call awaited.
    /// </remarks>
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task SubscribeAfterQueryAsync_UsesCachedData()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle).Build();

        var fetch = _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        handler.Release(0, "fetched");
        await fetch.WaitAsync(TestContext.Current.CancellationToken);

        var observer = _client.Subscribe(queryOptions, () => { });

        Assert.Equal("fetched", observer.Query.Data);
        Assert.True(observer.Query.IsResolved);
    }

    /// <remarks>
    /// The first fetch is awaited, not waited on through the query's state: the state reads
    /// resolved while the run is still in flight, and an invalidation that arrives then joins
    /// that run instead of starting the second fetch this test counts. The call's own task
    /// completes only once the run is over.
    /// </remarks>
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task InvalidateAfterQueryAsync_TriggersRefetch()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle)
            .ConfigureFetch(b => b.StaleTime(TimeSpan.FromSeconds(30)))
            .Build();

        var first = _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        handler.Release(0, "v1");
        await first.WaitAsync(TestContext.Current.CancellationToken);

        var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal(1, handler.Calls);
        Assert.Equal("v1", observer.Query.Data);

        _client.Invalidate(Key);

        Assert.Equal(2, handler.Calls);
        Assert.True(observer.Query.IsFetching);

        var idle = Signals.UntilAsync(observer.Query, q => q.IsIdle, TestContext.Current.CancellationToken);
        handler.Release(1, "v2");
        await idle;

        Assert.Equal("v2", observer.Query.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void QueryAsync_WithEnabledFalse_StillFetches()
    {
        var counter = new Counter();
        var queryOptions = QueryOptions.Create(Key, _ => Task.FromResult($"data{counter.Increment()}"))
            .Enabled(false)
            .Build();

        Discard(_client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));

        Assert.Equal(1, counter.Value);
        Assert.True(_client.FindQuery(Key)!.IsResolved);
    }
}
