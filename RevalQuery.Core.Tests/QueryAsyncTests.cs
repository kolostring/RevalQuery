using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class QueryAsyncTests : IDisposable
{
    private const string Key = "fetch";

    private readonly RevalClient _client;

    public QueryAsyncTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(),
            new RevalQueryOptions { TimeProvider = new FakeTimeProvider() });
    }

    public void Dispose() => _client.Dispose();

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_ReturnsData()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        var result = await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);

        Assert.Equal("data", result);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_ThrowsOnHandlerException()
    {
        var counter = new Counter();
        var queryOptions = QueryOptions.Create<string>(Key, _ =>
            {
                counter.Increment();
                throw new InvalidOperationException("handler-fail");
            })
            .ConfigureRetry(r => r.Retry(3, _ => TimeSpan.Zero))
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));

        Assert.Equal(4, counter.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_ConcurrentCalls_SameKey()
    {
        var handler = new GatedHandler<object>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle).Build();

        var t1 = _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        var t2 = _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        var t3 = _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        var value = new object();
        handler.Release(0, value);
        var results = await Task.WhenAll(t1, t2, t3).WaitAsync(TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Same(value, r));
        Assert.Equal(1, handler.Calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_ServesFreshDataFromCacheWithoutCallingTheHandler()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        Assert.Equal("data", await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));
        Assert.Equal("data", await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_RefetchesWhenTheDataIsStale()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .Build();

        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_JudgesStalenessByTheOptionsThisCallPassed()
    {
        var calls = 0;

        Task<string> Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("data");
        }

        await _client.QueryAsync(QueryOptions.Create(Key, Handler).Build(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);

        await _client.QueryAsync(QueryOptions.Create(Key, Handler)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build(), TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_WithNeverStale_ServesCacheEvenAfterInvalidation()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.NeverStale())
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        _client.Invalidate(Key);
        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_WithALongStaleTime_StillRefetchesAfterInvalidation()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromDays(365)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);
        _client.Invalidate(Key);
        await _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task QueryAsync_ServesFreshDataHeldBesideAnOlderFailure()
    {
        var calls = 0;

        Task<string> Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromResult("data")
                : Task.FromException<string>(new InvalidOperationException("boom"));

        var always = QueryOptions.Create(Key, Handler)
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        Assert.Equal("data", await _client.QueryAsync(always, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(always, TestContext.Current.CancellationToken));

        var cached = QueryOptions.Create(Key, Handler)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        Assert.Equal("data", await _client.QueryAsync(cached, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");
}
