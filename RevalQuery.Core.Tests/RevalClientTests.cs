using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Plugin;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

/// <remarks>
/// Without persistence, subscribing and invalidating fetch before they return, so the
/// assertions follow the calls straight away.
/// </remarks>
public class RevalClientTests : IDisposable
{
    private const string Key = "test";

    private readonly IServiceProvider _serviceProvider;
    private readonly RevalQueryOptions _options;
    private readonly RevalClient _client;

    public RevalClientTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
        _options = new RevalQueryOptions();
        _client = new RevalClient(_serviceProvider, _options);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public void Subscribe_TriggersHandler_And_ResolvesData()
    {
        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();
        using var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal("result", observer.Query.Data);
        Assert.True(observer.Query.IsIdle);
    }

    [Fact]
    public void Invalidate_Triggers_Refetch()
    {
        var calls = new Counter();
        var queryOptions = QueryOptions.Create(Key, _ => Task.FromResult($"res-{calls.Increment()}")).Build();
        using var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal("res-1", observer.Query.Data);

        _client.Invalidate(Key);

        Assert.Equal("res-2", observer.Query.Data);
        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public void Hierarchical_Invalidation_Works()
    {
        var users = new Counter();
        var user1 = new Counter();
        var user2 = new Counter();
        var unrelated = new Counter();
        using var usersObserver = _client.Subscribe(
            QueryOptions.Create(ValueTuple.Create("users"), _ => Task.FromResult($"users-{users.Increment()}")).Build(), () => { });
        using var user1Observer = _client.Subscribe(
            QueryOptions.Create(("users", 1), _ => Task.FromResult($"user1-{user1.Increment()}")).Build(), () => { });
        using var user2Observer = _client.Subscribe(
            QueryOptions.Create(("users", 2), _ => Task.FromResult($"user2-{user2.Increment()}")).Build(), () => { });
        using var unrelatedObserver = _client.Subscribe(
            QueryOptions.Create(ValueTuple.Create("users2"), _ => Task.FromResult($"users2-{unrelated.Increment()}")).Build(), () => { });
        Assert.Equal([1, 1, 1, 1], new[] { users.Value, user1.Value, user2.Value, unrelated.Value });

        _client.Invalidate(ValueTuple.Create("users"));

        Assert.Equal([2, 2, 2, 1], new[] { users.Value, user1.Value, user2.Value, unrelated.Value });
        Assert.Equal("user1-2", user1Observer.Query.Data);

        _client.Invalidate(("users", 1));

        Assert.Equal([2, 3, 2, 1], new[] { users.Value, user1.Value, user2.Value, unrelated.Value });
    }

    [Fact]
    public async Task GarbageCollection_Removes_State_After_TTL()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.Zero };
        await using var gcCollector = new TtlQueryGarbageCollector(options);
        using var client = new RevalClient(_serviceProvider, options, evictionPolicy: gcCollector);

        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        Assert.True(observer.Query.IsResolved);
        observer.Dispose();
        gcCollector.CollectExpiredEntries();

        Assert.Null(client.FindQuery(Key));
    }

    [Fact]
    public async Task Resubscribe_Cancels_Eviction()
    {
        var options = new RevalQueryOptions();
        options.CacheOptions = options.CacheOptions with { GcTime = TimeSpan.Zero };
        await using var gcCollector = new TtlQueryGarbageCollector(options);
        using var client = new RevalClient(_serviceProvider, options, evictionPolicy: gcCollector);
        var evicted = new List<ITuple>();
        gcCollector.OnEvictionRequired += evicted.Add;

        var queryOptions = QueryOptions.Create(Key, ResultHandler).Build();

        var observer = client.Subscribe(queryOptions, () => { });
        Assert.True(observer.Query.IsResolved);
        observer.Dispose();
        using var resubscribed = client.Subscribe(queryOptions, () => { });
        gcCollector.CollectExpiredEntries();

        Assert.Empty(evicted);
        Assert.NotNull(client.FindQuery(Key));
    }

    [Fact]
    public async Task RevalClient_Cancel_AbortsFetch()
    {
        var handler = new GatedHandler<string>();
        var queryOptions = QueryOptions.Create(Key, handler.Handle).Build();

        using var observer = _client.Subscribe(queryOptions, () => { });
        Assert.Equal(1, handler.Calls);
        Assert.True(observer.Query.IsFetching);

        await _client.CancelAsync(Key).WaitAsync(HangGuard);

        Assert.True(handler.Cancelled(0).IsCompleted);
        Assert.True(observer.Query.IsIdle);
        Assert.True(observer.Query.IsPending);
        Assert.Null(observer.Query.Data);
        Assert.Null(observer.Query.Exception);
        Assert.Equal(1, handler.Calls);
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
        using var observer = _client.Subscribe(queryOptions, () => { });
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

    private static Task<string> ResultHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
        => Task.FromResult("result");
}
