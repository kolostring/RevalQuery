using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class QueryAsyncDiscardedTests : IDisposable
{
    private const string Key = "discarded";
    private const string Key2 = "discarded2";

    private readonly RevalClient _client;

    public QueryAsyncDiscardedTests()
    {
        _client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
    }

    public void Dispose() => _client.Dispose();

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Discarded_Call_Still_Stores_Its_Data_In_The_Registry()
    {
        var handler = new GatedHandler<string>();

        Discard(_client.QueryAsync(QueryOptions.Create(Key, handler.Handle).Build(), TestContext.Current.CancellationToken));

        var state = _client.FindQuery<string>(Key)!;
        Assert.True(state.IsFetching);

        var resolved = Signals.UntilAsync(state, s => s.IsResolved, TestContext.Current.CancellationToken);
        handler.Release(0, "data");
        await resolved;

        Assert.Equal("data", state.Data);
    }

    /// <remarks>
    /// The freshness check and any refetch run synchronously inside <c>QueryAsync</c>, so the
    /// absence of a second call is settled when it returns.
    /// </remarks>
    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Second_Discarded_Call_Reuses_Fresh_Data_Rather_Than_Refetching()
    {
        var counter = new Counter();
        var queryOptions = QueryOptions.Create(Key,
                _ => Task.FromResult($"data{counter.Increment()}"))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

        Discard(_client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));

        var state = _client.FindQuery<string>(Key)!;
        Assert.True(state.IsResolved);
        Assert.Equal(1, counter.Value);

        Discard(_client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));

        Assert.Equal(1, counter.Value);
        Assert.Equal("data1", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Discarded_Call_Leaves_A_Handler_Failure_On_The_Query()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle)
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        var thrown = Record.Exception(() => Discard(_client.QueryAsync(queryOptions, TestContext.Current.CancellationToken)));
        Assert.Null(thrown);

        var state = _client.FindQuery<string>(Key)!;
        var failed = Signals.UntilAsync(state, s => s.IsException, TestContext.Current.CancellationToken);
        handler.Fail(0, new InvalidOperationException("fail"));
        await failed;

        Assert.IsType<InvalidOperationException>(state.Exception);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Same_Call_Awaited_Throws_Instead()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler)
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.QueryAsync(queryOptions, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Discarded_Calls_On_Different_Keys_Are_Independent()
    {
        var a = new GatedHandler<string>();
        var b = new GatedHandler<string>();

        Discard(_client.QueryAsync(QueryOptions.Create(Key, a.Handle).Build(), TestContext.Current.CancellationToken));
        Discard(_client.QueryAsync(QueryOptions.Create(Key2, b.Handle).Build(), TestContext.Current.CancellationToken));

        var state1 = _client.FindQuery<string>(Key)!;
        var state2 = _client.FindQuery<string>(Key2)!;

        var resolved2 = Signals.UntilAsync(state2, s => s.IsResolved, TestContext.Current.CancellationToken);
        b.Release(0, "data2");
        await resolved2;

        Assert.True(state1.IsFetching);

        var resolved1 = Signals.UntilAsync(state1, s => s.IsResolved, TestContext.Current.CancellationToken);
        a.Release(0, "data");
        await resolved1;

        Assert.Equal("data", state1.Data);
        Assert.Equal("data2", state2.Data);
    }

    private static Task<string> ThrowingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => throw new InvalidOperationException("fail");
}
