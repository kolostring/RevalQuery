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
    private const string Key = "test";
    private const string UsersKey = "users";

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
        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        Assert.Equal("result", observer.Query.Data);
        Assert.True(observer.Query.IsIdle);
    }

    [Fact]
    public async Task Invalidate_Triggers_Refetch()
    {
        var queryOptions = QueryOptions.Create(Key, UniqueResultHandler).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        var initialData = observer.Query.Data;
        _client.Invalidate(Key);
        await WaitUntil(observer.Query, s => s.IsResolved);
        Assert.NotSame(initialData, observer.Query.Data);
    }

    [Fact]
    public async Task Hierarchical_Invalidation_Works()
    {
        var obs1 = _client.Subscribe(QueryOptions.Create(UsersKey, User1Handler).Build(), () => { });
        var obs2 = _client.Subscribe(QueryOptions.Create("users2", User2Handler).Build(), () => { });
        await Task.WhenAll(WaitUntil(obs1.Query, s => s.IsResolved), WaitUntil(obs2.Query, s => s.IsResolved));
        Assert.Equal("u1", obs1.Query.Data);
        Assert.Equal("u2", obs2.Query.Data);
        _client.Invalidate(UsersKey);
        await Task.WhenAll(WaitUntil(obs1.Query, s => s.IsResolved), WaitUntil(obs2.Query, s => s.IsResolved));
        Assert.Equal("u1", obs1.Query.Data);
        Assert.Equal("u2", obs2.Query.Data);
    }

    [Fact]
    public async Task GarbageCollection_Removes_State_After_TTL()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.Zero };
        var gcCollector = new TtlQueryGarbageCollector(options);
        var client = new QueryClient(_serviceProvider, options, evictionPolicy: gcCollector);

        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        observer.Dispose();
        gcCollector.CollectExpiredEntries();
        var found = client.FindQuery(Key);
        Assert.Null(found);
    }

    [Fact]
    public async Task Resubscribe_Cancels_Eviction()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.FromSeconds(10) };
        var gcCollector = new TtlQueryGarbageCollector(options);
        var client = new QueryClient(_serviceProvider, options, evictionPolicy: gcCollector);

        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsResolved);
        observer.Dispose();
        client.Subscribe(queryOptions, () => { });
        gcCollector.CollectExpiredEntries();
        var found = client.FindQuery(Key);
        Assert.NotNull(found);
    }

    [Fact]
    public async Task QueryClient_Cancel_AbortsFetch()
    {
        var queryOptions = QueryOptions.Create(Key, CancelableHandler).Build();

        var observer = _client.Subscribe(queryOptions, () => { });
        await WaitUntil(observer.Query, s => s.IsFetching);
        _client.Cancel(Key);
        await WaitUntil(observer.Query, s => s.IsIdle);
        Assert.True(observer.Query.IsIdle);
    }

    [Fact]
    public void Plugin_Throws_On_NonStatic_Handler()
    {
        _options.QueryPluginsPipeline.Add(new QueryPluginHandlersStatelessValidation());
        var count = 0;
        var queryOptions = QueryOptions.Create(Key, _ => Task.FromResult(count++)).Build();
        Assert.Throws<InvalidOperationException>(() => _client.Subscribe(queryOptions, () => { }));
    }

    [Fact]
    public void Plugin_Allows_Static_Handler()
    {
        _options.QueryPluginsPipeline.Add(new QueryPluginHandlersStatelessValidation());
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();
        var observer = _client.Subscribe(queryOptions, () => { });
        Assert.NotNull(observer);
    }

    [Fact]
    public void QueryOptions_Create_With_String_Key_Succeeds()
    {
        var queryOptions = QueryOptions.Create(Key, StaticHandler).Build();
        Assert.IsType<ValueTuple<string>>(queryOptions.Key);
        Assert.Equal(Key, queryOptions.Key.Item1);
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

    private static Task<string> ResultHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("result");

    private static Task<string> UniqueResultHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult($"res{Guid.NewGuid()}");

    private static Task<string> User1Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("u1");

    private static Task<string> User2Handler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("u2");

    private static async Task<string> CancelableHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        try
        {
            await Task.Delay(1000, ctx.CancellationToken ?? default);
            return "completed";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }
}
