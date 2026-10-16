using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

/// <remarks>
/// Without persistence, subscribing and invalidating decide whether to fetch before they return,
/// so every assertion follows the call straight away. Each negative about a static query has a
/// non-static control proving the same path would have fetched.
/// </remarks>
public class StaticQueryTests
{
    private readonly FakeTimeProvider _clock = new();

    private RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock });

    private static QueryOptions<ValueTuple<string>, string> Options(string key, bool neverStale, Counter calls) =>
        QueryOptions.Create(key, _ => Task.FromResult($"call-{calls.Increment()}"))
            .ConfigureFetch(f =>
            {
                f.StaleTime(TimeSpan.Zero);
                if (neverStale) f.NeverStale();
            })
            .Build();

    [Fact]
    public void A_Static_Query_With_No_Data_Still_Fetches()
    {
        var calls = new Counter();
        using var client = NewClient();

        using var observer = client.Subscribe(Options("first-fetch", neverStale: true, calls), () => { });

        Assert.True(observer.Query.IsResolved);
        Assert.Equal("call-1", observer.Query.Data);
        Assert.Equal(1, calls.Value);
    }

    [Fact]
    public void A_Static_Query_Is_Not_Refetched_By_A_Later_Subscriber()
    {
        var calls = new Counter();
        using var client = NewClient();

        var first = client.Subscribe(Options("resubscribe", neverStale: true, calls), () => { });
        Assert.True(first.Query.IsResolved);
        first.Dispose();

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var second = client.Subscribe(Options("resubscribe", neverStale: true, calls), () => { });

        Assert.Equal(1, calls.Value);
        Assert.Equal("call-1", second.Query.Data);
    }

    [Fact]
    public void A_Non_Static_Query_Is_Refetched_By_A_Later_Subscriber()
    {
        var calls = new Counter();
        using var client = NewClient();

        var first = client.Subscribe(Options("resubscribe-control", neverStale: false, calls), () => { });
        Assert.True(first.Query.IsResolved);
        first.Dispose();

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var second = client.Subscribe(Options("resubscribe-control", neverStale: false, calls), () => { });

        Assert.Equal(2, calls.Value);
        Assert.Equal("call-2", second.Query.Data);
    }

    [Fact]
    public void Invalidating_A_Static_Query_Does_Not_Refetch_It()
    {
        var calls = new Counter();
        using var client = NewClient();

        using var observer = client.Subscribe(Options("invalidated-static", neverStale: true, calls), () => { });
        Assert.True(observer.Query.IsResolved);

        client.Invalidate("invalidated-static");

        Assert.Equal(1, calls.Value);
        Assert.Equal("call-1", observer.Query.Data);
    }

    [Fact]
    public void An_Invalidated_Static_Query_Still_Reports_Itself_Invalidated()
    {
        var calls = new Counter();
        using var client = NewClient();

        var state = client.GetOrCreateQuery(Options("marked-static", neverStale: true, calls));
        using var observer = client.Subscribe(Options("marked-static", neverStale: true, calls), () => { });
        Assert.True(observer.Query.IsResolved);

        client.Invalidate("marked-static");

        Assert.True(state.IsInvalidated);
        Assert.Equal(1, calls.Value);
    }

    [Fact]
    public void Invalidating_A_Non_Static_Query_Refetches_It()
    {
        var calls = new Counter();
        using var client = NewClient();

        using var observer = client.Subscribe(Options("invalidated-plain", neverStale: false, calls), () => { });
        Assert.True(observer.Query.IsResolved);

        client.Invalidate("invalidated-plain");

        Assert.Equal(2, calls.Value);
        Assert.Equal("call-2", observer.Query.Data);
    }

    [Fact]
    public void Invalidation_Leaves_LastUpdatedAt_Alone()
    {
        var calls = new Counter();
        using var client = NewClient();

        var state = client.GetOrCreateQuery(Options("clock", neverStale: true, calls));
        using var observer = client.Subscribe(Options("clock", neverStale: true, calls), () => { });
        Assert.True(observer.Query.IsResolved);

        var fetchedAt = state.LastUpdatedAt;
        Assert.NotEqual(DateTimeOffset.MinValue, fetchedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        client.Invalidate("clock");

        Assert.True(state.IsInvalidated);
        Assert.Equal(fetchedAt, state.LastUpdatedAt);
    }
}
