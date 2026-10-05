using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class StaticQueryTests
{
    private static int _calls;

    private static Task<string> CountingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult($"call-{call}");
    }

    private static QueryClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    private static QueryOptions<ValueTuple<string>, string> Options(string key, bool neverStale) =>
        QueryOptions.Create(key, CountingHandler)
            .ConfigureFetch(f =>
            {
                f.StaleTime(TimeSpan.Zero);
                if (neverStale) f.NeverStale();
            })
            .Build();

    [Fact]
    public async Task A_Static_Query_With_No_Data_Still_Fetches()
    {
        _calls = 0;
        using var client = NewClient();

        using var observer = client.Subscribe(Options("first-fetch", neverStale: true), () => { });

        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
        Assert.Equal("call-1", observer.Query.Data);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task A_Static_Query_Is_Not_Refetched_By_A_Later_Subscriber()
    {
        _calls = 0;
        using var client = NewClient();

        var first = client.Subscribe(Options("resubscribe", neverStale: true), () => { });
        await TestUtils.WaitForStateAsync(first.Query, s => s.IsResolved);
        first.Dispose();

        using var second = client.Subscribe(Options("resubscribe", neverStale: true), () => { });

        await Task.Delay(100);
        Assert.Equal(1, _calls);
        Assert.Equal("call-1", second.Query.Data);
    }

    [Fact]
    public async Task A_Non_Static_Query_Is_Refetched_By_A_Later_Subscriber()
    {
        _calls = 0;
        using var client = NewClient();

        var first = client.Subscribe(Options("resubscribe-control", neverStale: false), () => { });
        await TestUtils.WaitForStateAsync(first.Query, s => s.IsResolved);
        first.Dispose();

        using var second = client.Subscribe(Options("resubscribe-control", neverStale: false), () => { });

        await TestUtils.WaitUntilAsync(() => _calls == 2);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task Invalidating_A_Static_Query_Does_Not_Refetch_It()
    {
        _calls = 0;
        using var client = NewClient();

        using var observer = client.Subscribe(Options("invalidated-static", neverStale: true), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        client.Invalidate("invalidated-static");

        await Task.Delay(100);
        Assert.Equal(1, _calls);
        Assert.Equal("call-1", observer.Query.Data);
    }

    [Fact]
    public async Task An_Invalidated_Static_Query_Still_Reports_Itself_Invalidated()
    {
        _calls = 0;
        using var client = NewClient();

        var state = client.GetOrCreateQuery(Options("marked-static", neverStale: true));
        using var observer = client.Subscribe(Options("marked-static", neverStale: true), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        client.Invalidate("marked-static");

        await Task.Delay(100);
        Assert.True(state.IsInvalidated);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Invalidating_A_Non_Static_Query_Refetches_It()
    {
        _calls = 0;
        using var client = NewClient();

        using var observer = client.Subscribe(Options("invalidated-plain", neverStale: false), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        client.Invalidate("invalidated-plain");

        await TestUtils.WaitUntilAsync(() => _calls == 2);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task Invalidation_Leaves_LastUpdatedAt_Alone()
    {
        _calls = 0;
        using var client = NewClient();

        var state = client.GetOrCreateQuery(Options("clock", neverStale: true));
        using var observer = client.Subscribe(Options("clock", neverStale: true), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);

        var fetchedAt = state.LastUpdatedAt;
        Assert.NotEqual(DateTimeOffset.MinValue, fetchedAt);

        client.Invalidate("clock");

        await Task.Delay(100);
        Assert.Equal(fetchedAt, state.LastUpdatedAt);
    }
}
