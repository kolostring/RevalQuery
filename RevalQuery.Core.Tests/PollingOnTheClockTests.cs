using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class PollingOnTheClockTests
{
    private const string Key = "poll-clock";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new();
    private readonly Counter _calls = new();

    public PollingOnTheClockTests() => SynchronizationContext.SetSynchronizationContext(null);

    [Fact]
    public void Advancing_One_Interval_Refetches_Once()
    {
        using var client = NewClient();
        using var observer = client.Subscribe(Options(), () => { });
        Assert.Equal(1, _calls.Value);

        _clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Equal(1, _calls.Value);

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, _calls.Value);
    }

    [Fact]
    public void Advancing_Three_Intervals_Refetches_Three_Times()
    {
        using var client = NewClient();
        using var observer = client.Subscribe(Options(), () => { });

        _clock.Advance(Interval);
        _clock.Advance(Interval);
        _clock.Advance(Interval);

        Assert.Equal(4, _calls.Value);
    }

    [Fact]
    public void After_The_Last_Observer_Leaves_Advancing_Refetches_Nothing()
    {
        using var client = NewClient();
        var observer = client.Subscribe(Options(), () => { });
        _clock.Advance(Interval);
        Assert.Equal(2, _calls.Value);

        observer.Dispose();
        _clock.Advance(Interval * 3);

        Assert.Equal(2, _calls.Value);
    }

    [Fact]
    public void A_Poll_Firing_During_An_In_Flight_Fetch_Joins_It()
    {
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create(Key, handler.Handle)
            .ConfigureFetch(f => f.RefetchInterval(Interval))
            .Build();
        using var client = NewClient();
        using var observer = client.Subscribe(options, () => { });
        Assert.Equal(1, handler.Calls);

        _clock.Advance(Interval * 2);
        Assert.Equal(1, handler.Calls);

        handler.ReleaseAll("done");
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Resubscribing_From_Inside_The_Last_Unsubscribe_Keeps_Polling()
    {
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create(Key, handler.Handle)
            .ConfigureFetch(f => f.RefetchInterval(Interval))
            .Build();
        using var client = NewClient();
        var state = client.GetOrCreateQuery(options);
        IDisposable? second = null;
        state.OnLastSubscriberRemoved += _ => second ??= client.Subscribe(options, () => { });
        var first = client.Subscribe(options, () => { });
        await handler.Started(0).WaitAsync(HangGuard);

        first.Dispose();
        Assert.NotNull(second);
        var join = client.QueryAsync(options);
        handler.Release(0, "data");
        await join.WaitAsync(HangGuard);
        var before = handler.Calls;

        _clock.Advance(Interval);

        Assert.Equal(before + 1, handler.Calls);
        second!.Dispose();
    }

    private RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock });

    private QueryOptions<ValueTuple<string>, string> Options() =>
        QueryOptions.Create(Key, _ => { _calls.Increment(); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.RefetchInterval(Interval))
            .Build();
}
