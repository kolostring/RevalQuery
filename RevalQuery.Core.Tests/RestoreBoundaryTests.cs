using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class RestoreBoundaryTests
{
    private sealed record Frame(bool IsLoading, bool IsRestoring, bool IsFetching, bool IsPending);

    private sealed class Watcher
    {
        private readonly List<Frame> _frames = [];

        public Gate FirstFrameAfterRestore { get; } = new();

        public void Record(QueryState<ValueTuple<string>, string> state)
        {
            var frame = new Frame(state.IsLoading, state.IsRestoring, state.IsFetching, state.IsPending);
            lock (_frames) _frames.Add(frame);
            if (!frame.IsRestoring) FirstFrameAfterRestore.Open();
        }

        public Frame[] Snapshot()
        {
            lock (_frames) return [.. _frames];
        }
    }

    private static (QueryState<ValueTuple<string>, string> State, Watcher Watcher) Watch(
        RevalClient client, QueryOptions<ValueTuple<string>, string> options)
    {
        var state = client.GetOrCreateQuery(options);
        var watcher = new Watcher();
        client.Subscribe(options, () => watcher.Record(state));
        return (state, watcher);
    }

    private static RevalClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);

    [Fact]
    public async Task A_Query_With_Nothing_Stored_Never_Reports_A_Moment_With_No_Work_In_Progress()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("empty", handler.Handle).Build();

        var (state, watcher) = Watch(client, options);
        await persistence.LoadStarted.WaitAsync(HangGuard);

        Assert.True(state.IsRestoring && state.IsLoading);
        Assert.Equal(0, handler.Calls);

        persistence.ReleaseLoad();
        await watcher.FirstFrameAfterRestore.Task.WaitAsync(HangGuard);
        await handler.Started(0).WaitAsync(HangGuard);

        Assert.True(state.IsFetching);

        handler.Release(0, "from-network");
        await Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle);

        var frames = watcher.Snapshot();
        Assert.DoesNotContain(frames, f => f.IsPending && !f.IsLoading);
        Assert.Contains(frames, f => f.IsFetching);
        Assert.Equal("from-network", state.Data);
    }

    [Fact]
    public async Task A_Restore_Is_Not_Over_Until_The_Fetch_That_Follows_It_Has_Started()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("ordering", handler.Handle).Build();

        var (state, watcher) = Watch(client, options);

        Assert.True(state.IsRestoring);
        Assert.Equal(0, handler.Calls);

        persistence.ReleaseLoad();
        await state.RestoreCompleted.WaitAsync(HangGuard);

        Assert.True(state.IsFetching);
        Assert.False(state.IsRestoring);
        Assert.True(handler.Started(0).IsCompleted);
        Assert.Contains(watcher.Snapshot(), f => f.IsFetching && f.IsRestoring);

        handler.Release(0, "from-network");
        await Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle);

        Assert.Equal("from-network", state.Data);
    }

    [Fact]
    public async Task Stored_Data_That_Is_Still_Fresh_Ends_The_Restore_With_No_Fetch_At_All()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var calls = new Counter();
        var options = QueryOptions.Create<string>("fresh", _ =>
            {
                calls.Increment();
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(fetch => fetch.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        var (state, watcher) = Watch(client, options);
        await persistence.LoadStarted.WaitAsync(HangGuard);

        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)));
        await state.RestoreCompleted.WaitAsync(HangGuard);

        Assert.Equal(0, calls.Value);
        Assert.Equal("from-disk", state.Data);
        Assert.False(state.IsRestoring);

        await watcher.FirstFrameAfterRestore.Task.WaitAsync(HangGuard);

        Assert.DoesNotContain(watcher.Snapshot(), f => f.IsPending && !f.IsLoading);
    }

    [Fact]
    public async Task A_Subscriber_Arriving_After_The_Restore_Ended_Still_Fetches()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var calls = new Counter();
        var options = QueryOptions.Create<string>("late", _ =>
        {
            calls.Increment();
            return Task.FromResult("from-network");
        }).Build();

        var state = client.GetOrCreateQuery(options);
        persistence.ReleaseLoad();
        await state.RestoreCompleted.WaitAsync(HangGuard);

        Assert.False(state.IsRestoring);

        var observer = client.Subscribe(options, () => { });

        Assert.Equal("from-network", observer.Query.Data);
        Assert.Equal(1, calls.Value);
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
    public void A_Query_With_No_Persistence_Is_Never_Restoring()
    {
        using var client = NewClient();

        var options = QueryOptions.Create<string>("none", _ => Task.FromResult("from-network")).Build();

        var state = client.GetOrCreateQuery(options);
        var observer = client.Subscribe(options, () => { });

        Assert.False(state.IsRestoring);
        Assert.True(state.RestoreCompleted.IsCompleted);
        Assert.Equal("from-network", observer.Query.Data);
    }
}
