using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class QueryAsyncTests
{
    private const string Key = "fetch";

    private readonly QueryClient _client;

    public QueryAsyncTests()
    {
        _client = new QueryClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    [Fact]
    public async Task QueryAsync_ReturnsData()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        var result = await _client.QueryAsync(queryOptions);

        Assert.Equal("data", result);
    }

    [Fact]
    public async Task QueryAsync_ThrowsOnHandlerException()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(queryOptions));
    }

    [Fact]
    public async Task QueryAsync_ConcurrentCalls_SameKey()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        var results = await Task.WhenAll(
            _client.QueryAsync(queryOptions),
            _client.QueryAsync(queryOptions),
            _client.QueryAsync(queryOptions)
        );

        Assert.True(ReferenceEquals(results[0], results[1]));
        Assert.True(ReferenceEquals(results[1], results[2]));
    }

    [Fact]
    public async Task QueryAsync_ServesFreshDataFromCacheWithoutCallingTheHandler()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        Assert.Equal("data", await _client.QueryAsync(queryOptions));
        Assert.Equal("data", await _client.QueryAsync(queryOptions));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueryAsync_RefetchesWhenTheDataIsStale()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .Build();

        await _client.QueryAsync(queryOptions);
        await _client.QueryAsync(queryOptions);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task QueryAsync_JudgesStalenessByTheOptionsThisCallPassed()
    {
        var calls = 0;

        Task<string> Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("data");
        }

        await _client.QueryAsync(QueryOptions.Create(Key, Handler).Build());
        Assert.Equal(1, calls);

        await _client.QueryAsync(QueryOptions.Create(Key, Handler)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .Build());

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueryAsync_WithNeverStale_ServesCacheEvenAfterInvalidation()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.NeverStale())
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        await _client.QueryAsync(queryOptions);
        _client.Invalidate(Key);
        await _client.QueryAsync(queryOptions);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueryAsync_WithALongStaleTime_StillRefetchesAfterInvalidation()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromDays(365)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        await _client.QueryAsync(queryOptions);
        _client.Invalidate(Key);
        await _client.QueryAsync(queryOptions);

        Assert.Equal(2, calls);
    }

    [Fact]
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

        Assert.Equal("data", await _client.QueryAsync(always));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(always));

        var cached = QueryOptions.Create(Key, Handler)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        Assert.Equal("data", await _client.QueryAsync(cached));
        Assert.Equal(2, calls);
    }

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");

    private static Task<string> ThrowingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => throw new InvalidOperationException("handler-fail");
}
