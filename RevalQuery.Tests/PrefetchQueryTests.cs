using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class PrefetchQueryTests
{
    private const string Key = "prefetch";
    private const string Key2 = "prefetch2";

    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _options;
    private readonly QueryClient _client;

    public PrefetchQueryTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
        _options = new RevalQueryOptions();
        _client = new QueryClient(_serviceProvider, _options);
    }

    [Fact]
    public async Task PrefetchQuery_StoresDataInCache()
    {
        var queryOptions = QueryOptions.Create(Key, PrefetchedDataHandler).Build();

        _client.PrefetchQuery(queryOptions);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var state = _client.FindQuery(Key);
        Assert.NotNull(state);
        Assert.True(state.IsResolved);
    }

    [Fact]
    public async Task PrefetchQuery_ReusesExistingState()
    {
        var queryOptions = QueryOptions.Create(Key, DataHandler)
            .ConfigureFetch(b => b.StaleTime(TimeSpan.FromMinutes(5)))
            .Build();

        _client.PrefetchQuery(queryOptions);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);

        var firstData = ((QueryState<ValueTuple<string>, string>)_client.FindQuery(Key)!).Data;

        _client.PrefetchQuery(queryOptions);

        var state = _client.FindQuery(Key);
        Assert.NotNull(state);
        Assert.True(state.IsResolved);
        Assert.Equal(firstData, ((QueryState<ValueTuple<string>, string>)state).Data);
    }

    [Fact]
    public void PrefetchQuery_DoesNotThrowOnHandlerException()
    {
        var queryOptions = QueryOptions.Create(Key, ThrowingHandler).Build();

        var exception = Record.Exception(() => _client.PrefetchQuery(queryOptions));
        Assert.Null(exception);
    }

    [Fact]
    public async Task PrefetchQuery_MultipleKeys_Independent()
    {
        var queryOptions1 = QueryOptions.Create(Key, DataHandler).Build();
        var queryOptions2 = QueryOptions.Create(Key2, Data2Handler).Build();

        _client.PrefetchQuery(queryOptions1);
        _client.PrefetchQuery(queryOptions2);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key)!, s => s.IsResolved);
        await TestUtils.WaitForStateAsync(_client.FindQuery(Key2)!, s => s.IsResolved);

        var state1 = _client.FindQuery(Key);
        var state2 = _client.FindQuery(Key2);

        Assert.NotNull(state1);
        Assert.NotNull(state2);
    }

    private static Task<string> PrefetchedDataHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("prefetched-data");

    private static Task<string> DataHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data");

    private static Task<string> ThrowingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => throw new InvalidOperationException("fail");

    private static Task<string> Data2Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("data2");
}