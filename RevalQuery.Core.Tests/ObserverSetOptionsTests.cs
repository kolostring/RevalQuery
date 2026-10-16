using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class ObserverSetOptionsTests
{
    private readonly FakeTimeProvider _clock = new();

    private static readonly (string, int) Key1 = ("item", 1);
    private static readonly (string, int) Key2 = ("item", 2);

    private static QueryOptions<(string, int), string> Item(
        int id,
        Func<int, CancellationToken, Task<string>>? fetch = null,
        bool enabled = true,
        TimeSpan? refetchInterval = null,
        bool neverStale = false)
    {
        fetch ??= static (n, _) => Task.FromResult($"data-{n}");

        var builder = QueryOptions
            .Create<(string, int), string>(("item", id), ctx => fetch(ctx.Key.Item2, ctx.CancellationToken ?? default))
            .Enabled(enabled);

        if (refetchInterval is { } interval) builder.ConfigureFetch(f => f.RefetchInterval(interval));
        if (neverStale) builder.ConfigureFetch(f => f.NeverStale());

        return builder.Build();
    }

    private static Func<int, CancellationToken, Task<string>> Counting(Counter calls) => (n, _) =>
    {
        calls.Increment();
        return Task.FromResult($"data-{n}");
    };

    private RevalClient NewClient(ICacheEvictionPolicy? eviction = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, eviction);

    private static QueryState<(string, int), string> StateOf(QueryObserver<(string, int), string> observer) =>
        (QueryState<(string, int), string>)observer.Query;

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Same_Key_Reapplies_Options_Without_Refetching()
    {
        var calls = new Counter();
        using var client = NewClient();

        var options = Item(1, Counting(calls));

        using var observer = client.Subscribe(options, () => { });
        Assert.True(observer.Query.IsResolved);
        Assert.True(observer.Query.IsIdle);
        var before = observer.Query;
        Assert.Equal(1, calls.Value);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var rebuilt = options with { FetchOptions = FetchOptions.Create().StaleTime(TimeSpan.FromHours(1)).Build() };
        observer.SetOptions(rebuilt);

        Assert.Same(before, observer.Query);
        Assert.Equal(TimeSpan.FromHours(1), StateOf(observer).FetchOptions!.StaleTime);

        Assert.True(observer.Query.IsIdle);
        Assert.Equal(1, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Switching_To_A_Key_Held_Only_By_Disabled_Observers_Starts_Polling()
    {
        var key1Calls = new Counter();
        var key2Calls = new Counter();
        var interval = TimeSpan.FromSeconds(10);
        using var client = NewClient();

        Task<string> Fetch(int n, CancellationToken _)
        {
            (n == 2 ? key2Calls : key1Calls).Increment();
            return Task.FromResult($"data-{n}");
        }

        using var disabled = client.Subscribe(Item(2, Fetch, enabled: false, refetchInterval: interval), () => { });
        Assert.Equal(0, key2Calls.Value);

        using var switching = client.Subscribe(Item(1, Fetch, refetchInterval: interval), () => { });
        Assert.Equal(1, key1Calls.Value);

        switching.SetOptions(Item(2, Fetch, refetchInterval: interval));
        Assert.Equal(1, key2Calls.Value);

        _clock.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Equal(1, key2Calls.Value);

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, key2Calls.Value);

        _clock.Advance(interval);
        Assert.Equal(3, key2Calls.Value);
        Assert.Equal(1, key1Calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Same_Key_Enabling_A_Disabled_Observer_Fetches()
    {
        var calls = new Counter();
        using var client = NewClient();

        using var observer = client.Subscribe(Item(1, Counting(calls), enabled: false), () => { });
        Assert.Equal(0, calls.Value);

        observer.SetOptions(Item(1, Counting(calls), enabled: true));

        Assert.True(observer.Query.IsResolved);
        Assert.True(observer.Query.IsIdle);
        Assert.Equal(1, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_New_Key_Moves_The_Observer_To_That_Query_And_Fetches_It()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);
        var changes = new Counter();

        using var observer = client.Subscribe(Item(1), () => changes.Increment());
        Assert.True(observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2));
        var second = StateOf(observer);
        Assert.True(second.IsResolved);

        Assert.NotSame(first, second);
        Assert.Equal(Key2, second.Key);
        Assert.Equal("data-2", observer.Query.Data);

        Assert.False(first.HasObservers);
        Assert.True(second.HasObservers);
        Assert.Contains(Key1, eviction.Registered);
        Assert.Contains(Key2, eviction.Cancelled);

        Assert.Same(first, client.FindQuery(Key1));
        var seen = changes.Value;
        first.NotifyChanged();
        Assert.Equal(seen, changes.Value);

        second.NotifyChanged();
        Assert.Equal(seen + 1, changes.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_New_Key_That_Is_Disabled_Does_Not_Fetch()
    {
        var fetched = new List<int>();
        using var client = NewClient();

        Task<string> Fetch(int n, CancellationToken _)
        {
            lock (fetched) fetched.Add(n);
            return Task.FromResult($"data-{n}");
        }

        using var observer = client.Subscribe(Item(1, Fetch), () => { });
        Assert.True(observer.Query.IsResolved);

        observer.SetOptions(Item(2, Fetch, enabled: false));

        Assert.True(observer.Query.IsPending);
        Assert.False(observer.Query.IsEnabled);
        Assert.Equal([1], fetched);

        observer.SetOptions(Item(2, Fetch, enabled: true));
        Assert.True(observer.Query.IsResolved);

        Assert.Equal([1, 2], fetched);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Switching_Back_Within_GcTime_Reuses_The_Cached_Query()
    {
        var calls = new Counter();
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        using var observer = client.Subscribe(Item(1, Counting(calls), neverStale: true), () => { });
        Assert.True(observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2, Counting(calls), neverStale: true));
        Assert.True(observer.Query.IsResolved);
        Assert.Equal(2, calls.Value);

        eviction.Cancelled.Clear();
        observer.SetOptions(Item(1, Counting(calls), neverStale: true));

        Assert.Same(first, observer.Query);
        Assert.Equal("data-1", observer.Query.Data);
        Assert.Contains(Key1, eviction.Cancelled);
        Assert.Equal(2, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Switching_While_The_Old_Fetch_Runs_Neither_Cancels_It_Nor_Hears_From_It()
    {
        var old = new GatedHandler<string>();
        var changes = new Counter();
        using var client = NewClient();

        using var observer = client.Subscribe(
            QueryOptions.Create<(string, int), string>(Key1, old.Handle).Build(),
            () => changes.Increment());
        await old.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        var first = StateOf(observer);
        Assert.True(first.IsFetching);

        observer.SetOptions(Item(2));
        var second = StateOf(observer);

        Assert.True(second.IsResolved);
        Assert.Equal("data-2", observer.Query.Data);
        Assert.True(first.IsFetching);
        Assert.False(old.Token(0).IsCancellationRequested);

        var heard = changes.Value;

        var settled = Signals.UntilAsync(first, s => s.IsResolved && s.IsIdle, TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);
        old.Release(0, "data-1");
        await settled;

        Assert.Equal("data-1", first.Data);
        Assert.Equal(heard, changes.Value);
        Assert.Same(second, observer.Query);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Result_Type_Mismatch_Throws_Before_The_Observer_Moves()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);
        var changes = new Counter();

        using var observer = client.Subscribe(Item(1), () => changes.Increment());
        Assert.True(observer.Query.IsResolved);
        var first = StateOf(observer);

        await client.QueryAsync(QueryOptions.Create<(string, int), int>(Key2, static _ => Task.FromResult(7)).Build(), TestContext.Current.CancellationToken);
        eviction.Registered.Clear();

        var ex = Assert.Throws<InvalidOperationException>(() => observer.SetOptions(Item(2)));
        Assert.Contains("already registered", ex.Message);

        Assert.Same(first, observer.Query);
        Assert.True(first.HasObservers);
        Assert.DoesNotContain(Key1, eviction.Registered);
        Assert.False(client.FindQuery(Key2)!.HasObservers);

        var seen = changes.Value;
        first.NotifyChanged();
        Assert.Equal(seen + 1, changes.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Disposing_After_A_Switch_Releases_The_New_Query_And_Is_Idempotent()
    {
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        var observer = client.Subscribe(Item(1), () => { });
        Assert.True(observer.Query.IsResolved);

        observer.SetOptions(Item(2));
        var second = StateOf(observer);
        Assert.True(second.IsResolved);

        eviction.Registered.Clear();

        observer.Dispose();
        observer.Dispose();

        Assert.False(second.HasObservers);
        Assert.Equal([Key2], eviction.Registered.Cast<(string, int)>().ToList());
        Assert.False(client.FindQuery(Key1)!.HasObservers);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void SetOptions_After_The_Observer_Is_Disposed_Throws()
    {
        using var client = NewClient();
        var observer = client.Subscribe(Item(1), () => { });

        observer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(1)));
        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(2)));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void SetOptions_After_The_Client_Is_Disposed_Throws()
    {
        var client = NewClient();
        var observer = client.Subscribe(Item(1), () => { });

        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(1)));
        Assert.Throws<ObjectDisposedException>(() => observer.SetOptions(Item(2)));

        observer.Dispose();
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Switching_Back_To_An_Evicted_Key_Creates_A_Fresh_Query()
    {
        var calls = new Counter();
        var eviction = new RecordingEviction();
        using var client = NewClient(eviction);

        using var observer = client.Subscribe(Item(1, Counting(calls)), () => { });
        Assert.True(observer.Query.IsResolved);
        var first = StateOf(observer);

        observer.SetOptions(Item(2, Counting(calls)));
        Assert.True(observer.Query.IsResolved);

        eviction.Evict(Key1);
        Assert.Null(client.FindQuery(Key1));

        observer.SetOptions(Item(1, Counting(calls)));
        var again = StateOf(observer);
        Assert.True(again.IsResolved);

        Assert.NotSame(first, again);
        Assert.Same(again, client.FindQuery(Key1));
        Assert.True(again.HasObservers);
        Assert.Equal(3, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Observe_Creates_On_The_First_Call_Then_Reapplies_And_Switches()
    {
        using var client = NewClient();
        QueryObserver<(string, int), string>? slot = null;

        var first = client.Observe(ref slot, Item(1), () => { });
        Assert.NotNull(slot);
        var created = slot;
        Assert.Same(slot.Query, first);

        var again = client.Observe(ref slot, Item(1), () => { });
        Assert.Same(created, slot);
        Assert.Same(first, again);

        var moved = client.Observe(ref slot, Item(2), () => { });
        Assert.Same(created, slot);
        Assert.NotSame(first, moved);
        Assert.Equal(Key2, ((QueryState<(string, int), string>)moved).Key);
        Assert.False(first.HasObservers);

        Assert.True(moved.IsResolved);
        Assert.Equal("data-2", moved.Data);

        slot!.Dispose();
        Assert.False(moved.HasObservers);
    }

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
/// A probabilistic regression net, not a proof. The invariant it checks (a switch always lands
/// on a registered query, even against eager eviction) holds by lock: the attach in
/// <c>SetObserverOptions</c> and the observer check in <c>HandleEviction</c> run under the same
/// client lock, so no gate can hold the window open and no deterministic interleaving test is
/// possible. The net makes contention likely: it waits until the churn has completed a query
/// before the rounds start. Every wait in it fails loudly at the hang guard.
/// </summary>
[Collection("stress")]
public class ObserverSetOptionsRaceTests
{
    private const int Rounds = 2000;

    [Fact(Explicit = true, Timeout = HangGuardMilliseconds)]
    public async Task Switching_Against_Eager_Eviction_Always_Lands_On_A_Registered_Query()
    {
        var eviction = new EvictOnRelease();
        using var client = new RevalClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        static QueryOptions<(string, int), string> Item(int id) =>
            QueryOptions.Create<(string, int), string>(("item", id), static _ => Task.FromResult("data")).Build();

        QueryObserver<(string, int), string>? slot = null;
        client.Observe(ref slot, Item(0), () => { });

        var churning = new Gate();
        var gaveUp = new Counter();
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var id in new[] { 0, 1 })
                {
                    try
                    {
                        await client.QueryAsync(Item(id));
                        churning.Open();
                    }
                    catch (OperationCanceledException) { }
                    catch (InvalidOperationException e) when (e.Message.Contains("could not be run"))
                    {
                        // QueryAsync gives up after its worker is released too many times while
                        // the fetch starts. That is documented behaviour, not the invariant.
                        gaveUp.Increment();
                    }
                }
            }
        }, TestContext.Current.CancellationToken);

        Exception? churnError;
        try
        {
            await churning.Task.WaitAsync(TestContext.Current.CancellationToken);

            for (var round = 0; round < Rounds; round++)
            {
                var id = round % 2;
                var state = client.Observe(ref slot, Item(id), () => { });

                Assert.Same(state, client.FindQuery(("item", id)));
                Assert.True(state.HasObservers);
            }
        }
        finally
        {
            stop.Cancel();
            churnError = await Record.ExceptionAsync(() => churn.WaitAsync(TestContext.Current.CancellationToken));
            slot!.Dispose();
        }

        Assert.Null(churnError);
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
