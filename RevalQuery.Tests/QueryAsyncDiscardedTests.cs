using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class QueryAsyncDiscardedTests
{
    private const string Key = "discarded";
    private const string Key2 = "discarded2";

    private readonly RevalClient _client;

    public QueryAsyncDiscardedTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    [Fact]
    public async Task A_Discarded_Call_Still_Stores_Its_Data_In_The_Registry()
    {
        var queryOptions = QueryOptions.Create(Key, DataHandler).Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var state = _client.FindQuery(Key);
        Assert.NotNull(state);
        Assert.True(state.IsResolved);
    }

    [Fact]
    public async Task A_Second_Discarded_Call_Reuses_Fresh_Data_Rather_Than_Refetching()
    {
        var calls = 0;
        var queryOptions = QueryOptions.Create(Key,
                ctx => { Interlocked.Increment(ref calls); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var firstData = ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!).Data;

        TestUtils.Discard(_client.QueryAsync(queryOptions));
        await Task.Delay(50);

        var state = (QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!;
        Assert.Equal(firstData, state.Data);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_Discarded_Call_Leaves_A_Handler_Failure_On_The_Query()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler)
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        var thrown = Record.Exception(() => TestUtils.Discard(_client.QueryAsync(queryOptions)));
        Assert.Null(thrown);

        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsException);

        var state = _client.FindQuery(Key)!;
        Assert.True(state.IsException);
    }

    [Fact]
    public async Task The_Same_Call_Awaited_Throws_Instead()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler)
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(queryOptions));
    }

    [Fact]
    public async Task Discarded_Calls_On_Different_Keys_Are_Independent()
    {
        TestUtils.Discard(_client.QueryAsync(QueryOptions.Create(Key, DataHandler).Build()));
        TestUtils.Discard(_client.QueryAsync(QueryOptions.Create(Key2, Data2Handler).Build()));

        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key2)!, s => s.IsResolved);

        Assert.Equal("data", ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!).Data);
        Assert.Equal("data2", ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key2)!).Data);
    }

    private static Task<string> DataHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");

    private static Task<string> ThrowingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => throw new InvalidOperationException("fail");

    private static Task<string> Data2Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data2");
}
