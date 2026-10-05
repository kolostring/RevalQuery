using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class RestoreBoundaryTests
{
    private sealed class Store(PersistedQuery<string>? entry, TimeSpan delay) : IQueryPersistence
    {
        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return entry is null ? null : (PersistedQuery<TRes>)(object)entry;
        }

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed record Frame(bool IsLoading, bool IsRestoring, bool IsFetching, bool IsPending);

    private static (List<Frame> Frames, QueryObserver<ValueTuple<string>, string> Observer) Watch(
        QueryClient client, QueryOptions<ValueTuple<string>, string> options)
    {
        var frames = new List<Frame>();
        var gate = new object();
        QueryObserver<ValueTuple<string>, string>? observer = null;

        observer = client.Subscribe(options, () =>
        {
            var query = observer!.Query;
            var frame = new Frame(query.IsLoading, query.IsRestoring, query.IsFetching, query.IsPending);
            lock (gate) frames.Add(frame);
        });

        return (frames, observer);
    }

    [Fact]
    public async Task A_Query_With_Nothing_Stored_Never_Reports_A_Moment_With_No_Work_In_Progress()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(
            sp, new RevalQueryOptions(), persistence: new Store(null, TimeSpan.FromMilliseconds(150)));

        var options = QueryOptions.Create<string>("empty", async _ =>
        {
            await Task.Delay(150);
            return "from-network";
        }).Build();

        var (frames, _) = Watch(client, options);

        await Task.Delay(800);

        Assert.DoesNotContain(frames, f => f.IsPending && !f.IsLoading);
        Assert.Contains(frames, f => f.IsFetching);
        Assert.Equal("from-network", client.FindQuery<string>("empty")!.Data);
    }

    [Fact]
    public async Task A_Restore_Is_Not_Over_Until_The_Fetch_That_Follows_It_Has_Started()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(
            sp, new RevalQueryOptions(), persistence: new Store(null, TimeSpan.FromMilliseconds(150)));

        var options = QueryOptions.Create<string>("ordering", async _ =>
        {
            await Task.Delay(300);
            return "from-network";
        }).Build();

        var state = client.GetOrCreateQuery(options);
        var (frames, observer) = Watch(client, options);

        await state.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(observer.Query.IsFetching);
        Assert.False(observer.Query.IsRestoring);

        await Task.Delay(500);
        Assert.Equal("from-network", observer.Query.Data);
        Assert.NotEmpty(frames);
    }

    [Fact]
    public async Task Stored_Data_That_Is_Still_Fresh_Ends_The_Restore_With_No_Fetch_At_All()
    {
        var handlerCalls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(
            sp,
            new RevalQueryOptions(),
            persistence: new Store(
                new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)),
                TimeSpan.FromMilliseconds(150)));

        var options = QueryOptions.Create<string>("fresh", _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(fetch => fetch.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        var (frames, observer) = Watch(client, options);

        await Task.Delay(600);

        Assert.Equal(0, handlerCalls);
        Assert.Equal("from-disk", observer.Query.Data);
        Assert.False(observer.Query.IsRestoring);
        Assert.DoesNotContain(frames, f => f.IsPending && !f.IsLoading);
    }

    [Fact]
    public async Task A_Subscriber_Arriving_After_The_Restore_Ended_Still_Fetches()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(
            sp, new RevalQueryOptions(), persistence: new Store(null, TimeSpan.FromMilliseconds(100)));

        var options = QueryOptions.Create<string>("late", _ => Task.FromResult("from-network")).Build();

        var state = client.GetOrCreateQuery(options);
        await state.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRestoring);

        var observer = client.Subscribe(options, () => { });
        await Task.Delay(300);

        Assert.Equal("from-network", observer.Query.Data);
    }

    [Fact]
    public void A_Restore_That_Is_Already_Ending_Refuses_To_Take_Over_A_Decision()
    {
        var state = new QueryState<ValueTuple<string>, string>(
            ValueTuple.Create("closing"), _ => Task.FromResult("from-network"), null, null, null);

        state.BeginRestore();

        var deferredTooLate = false;
        var lateDecisionRan = false;

        Assert.True(state.TryDeferUntilRestored(() =>
        {
            deferredTooLate = state.TryDeferUntilRestored(() => lateDecisionRan = true);
        }));

        state.CompleteRestore();

        Assert.False(deferredTooLate);
        Assert.False(lateDecisionRan);
        Assert.False(state.IsRestoring);
    }

    [Fact]
    public async Task A_Query_With_No_Persistence_Is_Never_Restoring()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("none", _ => Task.FromResult("from-network")).Build();

        var state = client.GetOrCreateQuery(options);
        var observer = client.Subscribe(options, () => { });

        Assert.False(state.IsRestoring);
        Assert.True(state.RestoreCompleted.IsCompleted);

        await Task.Delay(200);
        Assert.Equal("from-network", observer.Query.Data);
    }
}
