using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// An observer owns which query it watches. Handing it new options is the one act that covers
/// both a re-render that changed nothing about the key and one that changed the key, and the
/// observer is the thing that moves, so a caller holding it never holds a stale reference.
/// </summary>
public class ObserverSetOptionsTests
{
    private static readonly (string, int) Key1 = ("item", 1);
    private static readonly (string, int) Key2 = ("item", 2);

    private static QueryOptions<(string, int), string> Item(
        int id,
        Func<int, CancellationToken, Task<string>>? fetch = null,
        bool enabled = true,
        TimeSpan? staleTime = null,
        TimeSpan? refetchInterval = null)
    {
        fetch ??= static (n, _) => Task.FromResult($"data-{n}");

        var builder = QueryOptions
            .Create<(string, int), string>(("item", id), ctx => fetch(ctx.Key.Item2, ctx.CancellationToken ?? default))
            .Enabled(enabled);

        if (staleTime is { } stale) builder.ConfigureFetch(f => f.StaleTime(stale));
        if (refetchInterval is { } interval) builder.ConfigureFetch(f => f.RefetchInterval(interval));

        return builder.Build();
    }

    private static QueryClient NewClient(ICacheEvictionPolicy? eviction = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

    private static QueryState<(string, int), string> StateOf(QueryObserver<(string, int), string> observer) =>
        (QueryState<(string, int), string>)observer.Query;

    [Fact]
    public async Task The_Same_Key_Reapplies_Options_Without_Refetching()
    {
        var calls = 0;
        using var client = NewClient();

        // Zero stale time, so the data is stale the moment it lands. A same-key re-render that
        // ran the staleness check would refetch here, and one that did not is what is asserted.
        var options = Item(1, (n, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult($"data-{n}");
        });

        using var observer = client.Subscribe(options, () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsFetching == false && observer.Query.IsResolved);
        var before = observer.Query;
        Assert.Equal(1, calls);

        var rebuilt = options with { FetchOptions = FetchOptions.Create().StaleTime(TimeSpan.FromHours(1)).Build() };
        observer.SetOptions(rebuilt);

        Assert.Same(before, observer.Query);
        Assert.Equal(TimeSpan.FromHours(1), StateOf(observer).FetchOptions!.StaleTime);

        // A refetch starts synchronously, so there is nothing to wait out before asserting.
        Assert.True(observer.Query.IsIdle);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Switching_To_A_Key_Held_Only_By_Disabled_Observers_Starts_Polling()
    {
        var calls = 0;
        var interval = TimeSpan.FromMilliseconds(40);
        using var client = NewClient();

        Task<string> Fetch(int n, CancellationToken _)
        {
            if (n == 2) Interlocked.Increment(ref calls);
            return Task.FromResult($"data-{n}");
        }

        using var disabled = client.Subscribe(Item(2, Fetch, enabled: false, refetchInterval: interval), () => { });
        using var switching = client.Subscribe(Item(1, Fetch, refetchInterval: interval), () => { });

        switching.SetOptions(Item(2, Fetch, refetchInterval: interval));

        // One fetch for the switch itself, then the loop the switch has to start.
        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref calls) >= 3, 2000);

        Assert.True(Volatile.Read(ref calls) >= 3);
    }

    [Fact]
    public async Task The_Same_Key_Enabling_A_Disabled_Observer_Fetches()
    {
        var calls = 0;
        using var client = NewClient();

        Task<string> Fetch(int n, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult($"data-{n}");
        }

        using var observer = client.Subscribe(Item(1, Fetch, enabled: false), () => { });
        Assert.Equal(0, calls);

        observer.SetOptions(Item(1, Fetch, enabled: true));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_New_Key_Moves_The_Observer_To_That_Query_And_Fetches_It()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);
        var changes = 0;

        using var observer = client.Subscribe(Item(1), () => Interlocked.Increment(ref changes));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2));
        var second = StateOf(observer);
        await TestUtils.WaitForStateAsync(second, s => second.IsResolved);

        Assert.NotSame(first, second);
        Assert.Equal(Key2, second.Key);
        Assert.Equal("data-2", observer.Query.Data);

        Assert.False(first.HasObservers);
        Assert.True(second.HasObservers);
        Assert.Contains(Key1, eviction.Registered);
        Assert.Contains(Key2, eviction.Cancelled);

        // The old query is still in the registry, cached, and no longer reaches the observer.
        Assert.Same(first, client.FindQuery(Key1));
        var seen = Volatile.Read(ref changes);
        first.NotifyChanged();
        Assert.Equal(seen, Volatile.Read(ref changes));

        second.NotifyChanged();
        Assert.Equal(seen + 1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task A_New_Key_That_Is_Disabled_Does_Not_Fetch()
    {
        var fetched = new List<int>();
        using var client = NewClient();

        Task<string> Fetch(int n, CancellationToken _)
        {
            lock (fetched) fetched.Add(n);
            return Task.FromResult($"data-{n}");
        }

        using var observer = client.Subscribe(Item(1, Fetch), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);

        // The dependent-query pattern: the key moves on before the value it needs has arrived.
        observer.SetOptions(Item(2, Fetch, enabled: false));

        Assert.True(observer.Query.IsPending);
        Assert.False(observer.Query.IsEnabled);
        Assert.Equal([1], fetched);

        observer.SetOptions(Item(2, Fetch, enabled: true));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);

        Assert.Equal([1, 2], fetched);
    }

    [Fact]
    public async Task Switching_Back_Within_GcTime_Reuses_The_Cached_Query()
    {
        var calls = 0;
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        Task<string> Fetch(int n, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult($"data-{n}");
        }

        // An hour's stale time, so coming back is a cache hit and not a refetch.
        var hour = TimeSpan.FromHours(1);

        using var observer = client.Subscribe(Item(1, Fetch, staleTime: hour), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2, Fetch, staleTime: hour));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);
        Assert.Equal(2, calls);

        eviction.Cancelled.Clear();
        observer.SetOptions(Item(1, Fetch, staleTime: hour));

        Assert.Same(first, observer.Query);
        Assert.Equal("data-1", observer.Query.Data);
        Assert.Contains(Key1, eviction.Cancelled);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Switching_While_The_Old_Fetch_Runs_Neither_Cancels_It_Nor_Hears_From_It()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldToken = CancellationToken.None;
        var changes = 0;
        using var client = NewClient();

        async Task<string> Fetch(int n, CancellationToken token)
        {
            if (n == 1)
            {
                oldToken = token;
                oldStarted.SetResult();
                await releaseOld.Task;
            }

            return $"data-{n}";
        }

        using var observer = client.Subscribe(Item(1, Fetch), () => Interlocked.Increment(ref changes));
        await oldStarted.Task;
        var first = StateOf(observer);
        Assert.True(first.IsFetching);

        observer.SetOptions(Item(2, Fetch));
        var second = StateOf(observer);
        await TestUtils.WaitForStateAsync(second, s => second.IsResolved);

        // Still running, and nobody asked it to stop.
        Assert.True(first.IsFetching);
        Assert.False(oldToken.IsCancellationRequested);
        Assert.Equal("data-2", observer.Query.Data);

        // Counted after the new query settled, so everything it said has been heard.
        var heard = Volatile.Read(ref changes);

        releaseOld.SetResult();
        await TestUtils.WaitForStateAsync(first, s => first.IsResolved && first.IsIdle);

        // The old query finished into its own state and the observer was not told.
        Assert.Equal("data-1", first.Data);
        Assert.Equal(heard, Volatile.Read(ref changes));
        Assert.Same(second, observer.Query);
    }

    [Fact]
    public async Task A_Result_Type_Mismatch_Throws_Before_The_Observer_Moves()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);
        var changes = 0;

        using var observer = client.Subscribe(Item(1), () => Interlocked.Increment(ref changes));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);
        var first = StateOf(observer);

        // The second key already belongs to another result type.
        await client.QueryAsync(QueryOptions.Create<(string, int), int>(Key2, static _ => Task.FromResult(7)).Build());
        eviction.Registered.Clear();

        var ex = Assert.Throws<InvalidOperationException>(() => observer.SetOptions(Item(2)));
        Assert.Contains("already registered", ex.Message);

        Assert.Same(first, observer.Query);
        Assert.True(first.HasObservers);
        Assert.DoesNotContain(Key1, eviction.Registered);
        Assert.False(client.FindQuery(Key2)!.HasObservers);

        // Still wired: the observer was never detached.
        var seen = Volatile.Read(ref changes);
        first.NotifyChanged();
        Assert.Equal(seen + 1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Disposing_After_A_Switch_Releases_The_New_Query_And_Is_Idempotent()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        var observer = client.Subscribe(Item(1), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);

        observer.SetOptions(Item(2));
        var second = StateOf(observer);
        await TestUtils.WaitForStateAsync(second, s => second.IsResolved);

        eviction.Registered.Clear();

        observer.Dispose();
        observer.Dispose();

        Assert.False(second.HasObservers);
        Assert.Equal([Key2], eviction.Registered.Cast<(string, int)>().ToList());
        Assert.False(client.FindQuery(Key1)!.HasObservers);
    }

    [Fact]
    public void SetOptions_After_The_Observer_Is_Disposed_Throws()
    {
        using var client = NewClient();
        var observer = client.Subscribe(Item(1), () => { });

        observer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(1)));
        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(2)));
    }

    [Fact]
    public void SetOptions_After_The_Client_Is_Disposed_Throws()
    {
        var client = NewClient();
        var observer = client.Subscribe(Item(1), () => { });

        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(1)));
        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(2)));

        // Teardown stays silent, whichever of the two went first.
        observer.Dispose();
    }

    [Fact]
    public async Task Switching_Back_To_An_Evicted_Key_Creates_A_Fresh_Query()
    {
        var calls = 0;
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        Task<string> Fetch(int n, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult($"data-{n}");
        }

        using var observer = client.Subscribe(Item(1, Fetch), () => { });
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2, Fetch));
        await TestUtils.WaitForStateAsync(observer.Query, s => observer.Query.IsResolved);

        // The eviction policy decides the old query's time is up while the observer is away.
        eviction.Evict(Key1);
        Assert.Null(client.FindQuery(Key1));

        observer.SetOptions(Item(1, Fetch));
        var again = StateOf(observer);
        await TestUtils.WaitForStateAsync(again, s => again.IsResolved);

        Assert.NotSame(first, again);
        Assert.Same(again, client.FindQuery(Key1));
        Assert.True(again.HasObservers);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Observe_Creates_On_The_First_Call_Then_Reapplies_And_Switches()
    {
        using var client = NewClient();
        QueryObserver<(string, int), string>? slot = null;

        var first = client.Observe(ref slot, Item(1), () => { });
        Assert.NotNull(slot);
        var created = slot;
        Assert.Same(slot.Query, first);

        // Same key: the slot is kept and so is the query.
        var again = client.Observe(ref slot, Item(1), () => { });
        Assert.Same(created, slot);
        Assert.Same(first, again);

        // New key: the slot is kept and what it returns has moved.
        var moved = client.Observe(ref slot, Item(2), () => { });
        Assert.Same(created, slot);
        Assert.NotSame(first, moved);
        Assert.Equal(Key2, ((QueryState<(string, int), string>)moved).Key);
        Assert.False(first.HasObservers);

        await TestUtils.WaitForStateAsync(moved, s => moved.IsResolved);
        Assert.Equal("data-2", moved.Data);

        slot!.Dispose();
        Assert.False(moved.HasObservers);
    }

    /// <summary>
    /// Records what the client asks of its eviction policy, and lets a test play the part of
    /// the timer that would eventually call back.
    /// </summary>
    private sealed class RecordingEviction : ICacheEvictionPolicy
    {
        public List<ITuple> Registered { get; } = [];
        public List<ITuple> Cancelled { get; } = [];

        public event Action<ITuple>? OnEvictionRequired;

        public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) =>
            Registered.Add(key);

        public void CancelEviction(ITuple key) => Cancelled.Add(key);

        public void Evict(ITuple key) => OnEvictionRequired?.Invoke(key);

        public Task StopAsync() => Task.CompletedTask;
    }
}

/// <summary>
/// Runs alone for the reason <see cref="StressCollection"/> gives: the window is reached by
/// repetition across threads, and that starves any clock-bound test beside it.
/// </summary>
[Collection("stress")]
public class ObserverSetOptionsRaceTests
{
    private const int Rounds = 2000;

    [Fact]
    public async Task Switching_Against_Eager_Eviction_Always_Lands_On_A_Registered_Query()
    {
        // Evicts the instant a query is left unobserved, so every switch away from a key races
        // an eviction of it, and every switch back races the re-creation of what was evicted.
        var eviction = new EvictOnRelease();
        using var client = new QueryClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        static QueryOptions<(string, int), string> Item(int id) =>
            QueryOptions.Create<(string, int), string>(("item", id), static _ => Task.FromResult("data")).Build();

        QueryObserver<(string, int), string>? slot = null;
        client.Observe(ref slot, Item(0), () => { });

        // A second party churning the same keys: one-off fetches own no observer, so each
        // release hands the key to the eviction policy as well.
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var id in new[] { 0, 1 })
                {
                    try { await client.QueryAsync(Item(id)); }
                    catch (OperationCanceledException) { }
                }
            }
        });

        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var id = round % 2;
                var state = client.Observe(ref slot, Item(id), () => { });

                // The state the observer reports is the state the registry holds for that key,
                // and it is observed. An observer left on an evicted state would fail the
                // first, and a query dropped from under the observer the second.
                Assert.Same(state, client.FindQuery(("item", id)));
                Assert.True(state.HasObservers);
            }
        }
        finally
        {
            stop.Cancel();
            await churn;
            slot!.Dispose();
        }
    }

    private sealed class EvictOnRelease : ICacheEvictionPolicy
    {
        public event Action<ITuple>? OnEvictionRequired;

        public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) =>
            OnEvictionRequired?.Invoke(key);

        public void CancelEviction(ITuple key) { }

        public Task StopAsync() => Task.CompletedTask;
    }
}
