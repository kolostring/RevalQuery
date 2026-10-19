using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class ReviewOfA700df3Tests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Cancelled_Fetch_Does_Not_Return_Data_It_Never_Fetched()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("cancelled", handler.Handle).Build();

        var fetch = client.QueryAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        await client.CancelAsync("cancelled").WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Restore_Does_Not_Clear_A_Failed_Query()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);

        var options = QueryOptions.Create<string>(
            "failed", _ => Task.FromException<string>(new InvalidOperationException("boom")))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(options, TestContext.Current.CancellationToken));

        var state = client.FindQuery<string>("failed")!;
        Assert.True(state.IsException);
        Assert.True(state.IsRestoring);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(_clock.GetUtcNow())));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(state.IsException);
        Assert.NotNull(state.Exception);
        Assert.False(state.IsResolved);
        Assert.Null(state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Successful_Fetch_Clears_An_Earlier_Failure()
    {
        using var client = NewClient();

        var shouldFail = true;

        var options = QueryOptions.Create("recovering", _ => shouldFail
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("value"))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(options, TestContext.Current.CancellationToken));

        shouldFail = false;
        Assert.Equal("value", await client.QueryAsync(options, TestContext.Current.CancellationToken));

        var state = client.FindQuery<string>("recovering")!;
        Assert.True(state.IsResolved);
        Assert.Null(state.Exception);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Failed_Fetch_Still_Throws_When_A_Restore_Lands_During_It()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("racing", handler.Handle).ConfigureRetry(r => r.Retry(0)).Build();

        var fetch = client.QueryAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(_clock.GetUtcNow())));
        var state = client.FindQuery<string>("racing")!;
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(state.IsResolved);
        Assert.Equal("from-disk", state.Data);

        handler.Fail(0, new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetch.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(state.IsException);
    }

    private RevalClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, persistence: persistence);
}
