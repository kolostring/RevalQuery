using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Plugin;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class QueryClientTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _options;
    private readonly QueryClient _client;

    public QueryClientTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
        _options = new RevalQueryOptions();
        _client = new QueryClient(_serviceProvider, _options);
    }

    [Fact]
    public async Task Subscribe_TriggersHandler_And_ResolvesData()
    {
        var key = (1, "test");
        var expectedData = "result";
        var callCount = 0;
        var queryOptions = QueryOptions.Create(key, ctx => { callCount++; return Task.FromResult(expectedData); }).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        Assert.Equal(expectedData, observer.Query.Data);
        Assert.True(observer.Query.IsIdle);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Invalidate_Triggers_Refetch()
    {
        var key = (1, "test");
        var callCount = 0;
        var queryOptions = QueryOptions.Create(key, ctx => { callCount++; return Task.FromResult($"res {callCount}"); }).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        Assert.Equal(1, callCount);
        _client.Invalidate(key);
        await WaitUntil(observer.Query, s => s.IsResolved && callCount == 2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Hierarchical_Invalidation_Works()
    {
        var key1 = ("users", 1);
        var key2 = ("users", 2);
        var callCount1 = 0;
        var callCount2 = 0;
        var obs1 = _client.Subscribe(QueryOptions.Create(key1, _ => { callCount1++; return Task.FromResult("u1"); }).Build(), () => { });
        var obs2 = _client.Subscribe(QueryOptions.Create(key2, _ => { callCount2++; return Task.FromResult("u2"); }).Build(), () => { });
        await Task.WhenAll(WaitUntil(obs1.Query, s => s.IsResolved), WaitUntil(obs2.Query, s => s.IsResolved));
        Assert.Equal(1, callCount1);
        Assert.Equal(1, callCount2);
        _client.Invalidate("users");
        await Task.WhenAll(WaitUntil(obs1.Query, s => s.IsResolved && callCount1 == 2), WaitUntil(obs2.Query, s => s.IsResolved && callCount2 == 2));
        Assert.Equal(2, callCount1);
        Assert.Equal(2, callCount2);
    }

    [Fact]
    public async Task GarbageCollection_Removes_State_After_TTL()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.Zero };
        var gcCollector = new TtlQueryGarbageCollector(options);
        var client = new QueryClient(_serviceProvider, options, evictionPolicy: gcCollector);

        var key = (1, "gc-test");
        var queryOptions = QueryOptions.Create(key, _ => Task.FromResult("result")).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        observer.Dispose();
        gcCollector.CollectExpiredEntries();
        var found = client.FindQuery(key);
        Assert.Null(found);
    }

    [Fact]
    public async Task Resubscribe_Cancels_Eviction()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.FromSeconds(10) };
        var gcCollector = new TtlQueryGarbageCollector(options);
        var client = new QueryClient(_serviceProvider, options, evictionPolicy: gcCollector);

        var key = (1, "resub-test");
        var queryOptions = QueryOptions.Create(key, _ => Task.FromResult("result")).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        observer.Dispose();
        client.Subscribe(queryOptions, () => { });
        gcCollector.CollectExpiredEntries();
        var found = client.FindQuery(key);
        Assert.NotNull(found);
    }

    [Fact]
    public async Task QueryClient_Cancel_AbortsFetch()
    {
        var key = (1, "cancel-test");
        var isCanceled = false;

        var queryOptions = QueryOptions.Create(key, async ctx =>
        {
            try
            {
                await Task.Delay(1000, ctx.CancellationToken ?? default);
                return "completed";
            }
            catch (OperationCanceledException)
            {
                isCanceled = true;
                throw;
            }
        }).Build();

        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsFetching);
        _client.Cancel(key);
        await WaitUntil(observer.Query, s => s.IsIdle);
        Assert.True(isCanceled);
        Assert.True(observer.Query.IsIdle);
    }

    [Fact]
    public void Plugin_Throws_On_NonStatic_Handler()
    {
        _options.QueryPluginsPipeline.Add(new QueryPluginHandlersStatelessValidation());
        var key = ValueTuple.Create("static-test");
        var count = 0;
        var queryOptions = QueryOptions.Create(key, _ => Task.FromResult(count++)).Build();
        Assert.Throws<InvalidOperationException>(() => _client.Subscribe(queryOptions, () => { }));
    }

    [Fact]
    public void Plugin_Allows_Static_Handler()
    {
        _options.QueryPluginsPipeline.Add(new QueryPluginHandlersStatelessValidation());
        var key = ValueTuple.Create("static-ok-test");
        var queryOptions = QueryOptions.Create(key, StaticHandler).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        Assert.NotNull(observer);
    }

    [Fact]
    public void QueryOptions_Create_With_String_Key_Succeeds()
    {
        var key = "string-key";
        var queryOptions = QueryOptions.Create(key, StaticHandler).Build();
        Assert.IsType<ValueTuple<string>>(queryOptions.Key);
        Assert.Equal(key, queryOptions.Key.Item1);
    }

    private static Task<string> StaticHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("ok");

    private static async Task WaitUntil<T>(T state, Func<T, bool> predicate) where T : IObservableQueryState
    {
        if (predicate(state)) return;
        var tcs = new TaskCompletionSource();
        Action handler = () => { if (predicate(state)) tcs.TrySetResult(); };
        state.OnChanged += handler;
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            state.OnChanged -= handler;
        }
    }
}
