using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class LoadingDuringRestoreTests
{
    [Fact]
    public async Task A_Query_Waiting_On_A_Restore_Is_Loading_But_Not_Fetching()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);

        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("restoring", handler.Handle)
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        var observer = client.Subscribe(options, () => { });
        var state = client.FindQuery<string>("restoring")!;

        await persistence.LoadStarted.WaitAsync(HangGuard);

        Assert.True(state.IsRestoring);
        Assert.True(state.IsLoading);
        Assert.False(state.IsFetching);
        Assert.True(state.IsPending);

        Assert.True(state.CanFetch);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)));
        await state.RestoreCompleted.WaitAsync(HangGuard);

        Assert.False(state.IsRestoring);
        Assert.False(state.IsLoading);
        Assert.True(state.IsResolved);
        Assert.Equal("from-disk", state.Data);
        Assert.Equal(0, handler.Calls);
        observer.Dispose();
    }

    [Fact]
    public async Task A_Restore_That_Finds_Nothing_Stops_Loading_And_Notifies()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);

        var options = QueryOptions.Create<string>("empty", _ => Task.FromResult("from-network"))
            .Enabled(false)
            .Build();

        var notified = new Gate();
        (bool IsLoading, bool IsRestoring, bool IsPending)? seen = null;
        QueryObserver<ValueTuple<string>, string>? observer = null;
        var state = client.GetOrCreateQuery(options);
        observer = client.Subscribe(options, () =>
        {
            if (notified.Task.IsCompleted) return;
            seen = (state.IsLoading, state.IsRestoring, state.IsPending);
            notified.Open();
        });

        await persistence.LoadStarted.WaitAsync(HangGuard);
        Assert.True(state.IsLoading);

        persistence.ReleaseLoad();
        await notified.Task.WaitAsync(HangGuard);

        Assert.Equal((false, false, true), seen);
        Assert.False(state.IsLoading);
        Assert.False(state.IsRestoring);
        Assert.True(state.IsPending);
        observer.Dispose();
    }

    [Fact]
    public void A_Query_Without_Persistence_Is_Never_Restoring()
    {
        using var client = NewClient(persistence: null);

        var options = QueryOptions.Create<string>("plain", _ => Task.FromResult("value")).Build();
        var observer = client.Subscribe(options, () => { });

        Assert.False(observer.Query.IsRestoring);
        Assert.True(observer.Query.IsResolved);
        Assert.False(observer.Query.IsLoading);
    }

    private static RevalClient NewClient(IQueryPersistence? persistence) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);
}
