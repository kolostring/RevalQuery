using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class FetchQueryAsyncTests
{
    private const string Key = "fetch";

    private readonly QueryClient _client;

    public FetchQueryAsyncTests()
    {
        _client = new QueryClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    [Fact]
    public async Task FetchQueryAsync_ReturnsData()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        var result = await _client.FetchQueryAsync(queryOptions);

        Assert.Equal("data", result);
    }

    [Fact]
    public async Task FetchQueryAsync_ThrowsOnHandlerException()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.FetchQueryAsync(queryOptions));
    }

    [Fact]
    public async Task FetchQueryAsync_ConcurrentCalls_SameKey()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();

        var results = await Task.WhenAll(
            _client.FetchQueryAsync(queryOptions),
            _client.FetchQueryAsync(queryOptions),
            _client.FetchQueryAsync(queryOptions)
        );

        Assert.True(ReferenceEquals(results[0], results[1]));
        Assert.True(ReferenceEquals(results[1], results[2]));
    }

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");

    private static Task<string> ThrowingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => throw new InvalidOperationException("handler-fail");

}