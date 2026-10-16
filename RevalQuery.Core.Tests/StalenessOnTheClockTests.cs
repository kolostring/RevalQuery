using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class StalenessOnTheClockTests
{
    private const string Key = "clock";
    private static readonly TimeSpan StaleTime = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _clock;
    private readonly Counter _calls = new();

    public StalenessOnTheClockTests()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        _clock = new FakeTimeProvider();
    }

    [Fact]
    public async Task Data_Is_Fresh_Just_Before_Its_StaleTime()
    {
        using var client = NewClient();

        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);
        _clock.Advance(StaleTime - TimeSpan.FromTicks(1));
        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);

        Assert.Equal(1, _calls.Value);
    }

    [Fact]
    public async Task Data_Is_Stale_When_Its_Age_Equals_Its_StaleTime()
    {
        using var client = NewClient();

        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);
        _clock.Advance(StaleTime);
        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);

        Assert.Equal(2, _calls.Value);
    }

    [Fact]
    public async Task Data_Is_Stale_After_Its_StaleTime()
    {
        using var client = NewClient();

        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);
        _clock.Advance(StaleTime + TimeSpan.FromTicks(1));
        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);

        Assert.Equal(2, _calls.Value);
    }

    [Fact]
    public async Task A_Zero_StaleTime_Is_Stale_With_The_Clock_Frozen()
    {
        using var client = NewClient();

        await client.QueryAsync(Options(TimeSpan.Zero)).WaitAsync(HangGuard);
        await client.QueryAsync(Options(TimeSpan.Zero)).WaitAsync(HangGuard);

        Assert.Equal(2, _calls.Value);
    }

    [Fact]
    public async Task A_Fetch_Is_Saved_With_The_Time_On_The_Clock()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        persistence.ReleaseLoad();
        var fetchedAt = _clock.GetUtcNow();

        await client.QueryAsync(Options(StaleTime)).WaitAsync(HangGuard);
        var save = await persistence.Saved(0).WaitAsync(HangGuard);

        Assert.Equal(fetchedAt, ((PersistedQuery<string>)save.Entry).Freshness.LastUpdatedAt);
    }

    [Fact]
    public async Task Restored_Data_Younger_Than_Its_StaleTime_On_The_Clock_Is_Not_Refetched()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var fetchedAt = _clock.GetUtcNow() - StaleTime + TimeSpan.FromTicks(1);
        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(fetchedAt)));

        var observer = client.Subscribe(Options(StaleTime), () => { });
        await Signals.UntilAsync(observer.Query, q => !q.IsRestoring).WaitAsync(HangGuard);

        Assert.Equal("from-disk", observer.Query.Data);
        Assert.False(observer.Query.IsFetching);
        Assert.Equal(0, _calls.Value);
    }

    [Fact]
    public async Task Restored_Data_As_Old_As_Its_StaleTime_On_The_Clock_Is_Refetched()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var fetchedAt = _clock.GetUtcNow() - StaleTime;
        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(fetchedAt)));

        var observer = client.Subscribe(Options(StaleTime), () => { });
        await Signals.UntilAsync(observer.Query, q => q.Data == "data").WaitAsync(HangGuard);

        Assert.Equal(1, _calls.Value);
    }

    private RevalClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(),
            new RevalQueryOptions { TimeProvider = _clock },
            persistence: persistence);

    private QueryOptions<ValueTuple<string>, string> Options(TimeSpan staleTime) =>
        QueryOptions.Create(Key, _ => { _calls.Increment(); return Task.FromResult("data"); })
            .ConfigureFetch(f => f.StaleTime(staleTime))
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();
}
