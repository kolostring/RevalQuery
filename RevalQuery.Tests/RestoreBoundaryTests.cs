using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// A restore ends when the query knows what follows the stored data, not when the store
/// answers. See docs/adr/0006.
/// </summary>
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

    /// <summary>
    /// Every state an observer was shown, sampled at the moment it was notified.
    /// </summary>
    private sealed record Frame(bool IsLoading, bool IsRestoring, bool IsFetching, bool IsPending);

    private static (List<Frame> Frames, QueryObserver<string> Observer) Watch(
        QueryClient client, QueryOptions<ValueTuple<string>, string> options)
    {
        var frames = new List<Frame>();
        var gate = new Lock();
        QueryObserver<string>? observer = null;

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

        // The bug this guards: the restore used to end before the worker decided to fetch, so
        // one notification landed with the query pending and nothing running. A component
        // branching on IsLoading rendered its empty state for that frame.
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

        // Taken before subscribing so the test can await the restore itself. RestoreCompleted
        // is on the concrete state, not on IQueryState: a consumer gets IsRestoring, and
        // handing one a task to await invites exactly the hang the persistence contract bans.
        var state = client.GetOrCreateQuery(options);
        var (frames, observer) = Watch(client, options);

        await state.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(2));

        // The moment the restore reports itself over, the fetch it decided on is already
        // running. That is what leaves no gap for an empty frame to appear in.
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
                new PersistedQuery<string>("from-disk", DateTimeOffset.UtcNow),
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

        // Creating the query starts its restore. Nothing is subscribed, so no decision is
        // deferred and the restore ends on its own when the store answers.
        var state = client.GetOrCreateQuery(options);
        await state.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRestoring);

        var observer = client.Subscribe(options, () => { });
        await Task.Delay(300);

        Assert.Equal("from-network", observer.Query.Data);
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
