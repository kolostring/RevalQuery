using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class SetQueryDataTests
{
    private static readonly TimeSpan StaleTime = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _clock = new();
    private readonly Counter _calls = new();

    private RevalClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(),
            new RevalQueryOptions { TimeProvider = _clock },
            persistence: persistence);

    private QueryOptions<ValueTuple<string>, string> Options(
        string key = "set", TimeSpan? staleTime = null, bool neverStale = false) =>
        QueryOptions.Create(key, _ => { _calls.Increment(); return Task.FromResult("fetched"); })
            .ConfigureFetch(f =>
            {
                f.StaleTime(staleTime ?? StaleTime);
                if (neverStale) f.NeverStale();
            })
            .ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)))
            .Build();

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Setting_A_Value_On_A_Missing_Query_Creates_It_Resolved_Without_Fetching()
    {
        using var client = NewClient();

        client.SetQueryData(Options(), "seeded");

        var state = client.FindQuery<string>("set")!;
        Assert.True(state.IsResolved);
        Assert.Equal("seeded", state.Data);
        Assert.True(state.IsIdle);
        Assert.Equal(0, _calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_Updater_Receives_The_Current_Data()
    {
        using var client = NewClient();
        client.SetQueryData(Options(), "one");

        string? seen = null;
        client.SetQueryData(Options(), current =>
        {
            seen = current;
            return current + "-two";
        });

        Assert.Equal("one", seen);
        Assert.Equal("one-two", client.FindQuery<string>("set")!.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_Updater_On_A_Missing_Query_Receives_Null()
    {
        using var client = NewClient();
        var invoked = false;
        string? seen = "unset";

        client.SetQueryData(Options(), current =>
        {
            invoked = true;
            seen = current;
            return "made";
        });

        Assert.True(invoked);
        Assert.Null(seen);
        Assert.Equal("made", client.FindQuery<string>("set")!.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_Updater_Returning_Null_Creates_Nothing()
    {
        using var client = NewClient();

        client.SetQueryData(Options(), _ => null);

        Assert.Null(client.FindQuery<string>("set"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_Updater_Returning_Null_Leaves_Existing_Data_Alone()
    {
        using var client = NewClient();
        client.SetQueryData(Options(), "kept");
        var state = client.StateOf<string>("set");
        var stamped = state.LastUpdatedAt;
        _clock.Advance(TimeSpan.FromMinutes(1));

        client.SetQueryData(Options(), _ => null);

        Assert.Equal("kept", state.Data);
        Assert.Equal(stamped, state.LastUpdatedAt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Options_Of_A_Creating_Set_Are_Kept()
    {
        using var client = NewClient();

        client.SetQueryData(Options(staleTime: StaleTime), "first");

        Assert.Equal(StaleTime, client.StateOf<string>("set").FetchOptions!.StaleTime);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Options_Apply_Only_When_The_Set_Creates_The_Query()
    {
        using var client = NewClient();
        client.SetQueryData(Options(staleTime: StaleTime), "first");

        client.SetQueryData(Options(staleTime: TimeSpan.FromHours(1)), "second");

        var state = client.StateOf<string>("set");
        Assert.Equal("second", state.Data);
        Assert.Equal(StaleTime, state.FetchOptions!.StaleTime);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Set_Resolves_A_Pending_Query_And_Notifies_Observers()
    {
        using var client = NewClient();
        var notified = 0;
        var options = QueryOptions.Create<string>("set", _ => Task.FromResult("fetched")).Enabled(false).Build();
        using var observer = client.Subscribe(options, () => notified++);
        Assert.True(observer.Query.IsPending);
        var before = notified;

        client.SetQueryData(options, "optimistic");

        Assert.True(observer.Query.IsResolved);
        Assert.Equal("optimistic", observer.Query.Data);
        Assert.True(notified > before);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Set_Clears_The_Error_And_The_Invalidated_Flag()
    {
        using var client = NewClient();
        client.SetQueryData(Options(), "first");
        var state = client.StateOf<string>("set");
        state.ApplyFailed(new InvalidOperationException("boom"));
        client.Invalidate("set");
        Assert.True(state.IsException);
        Assert.True(state.IsInvalidated);

        client.SetQueryData(Options(), "second");

        Assert.True(state.IsResolved);
        Assert.Null(state.Exception);
        Assert.False(state.IsInvalidated);
        Assert.Equal("second", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Set_Data_Is_Fresh_Until_StaleTime_From_Its_Timestamp()
    {
        using var client = NewClient();
        client.SetQueryData(Options(), "set-data");
        var state = client.StateOf<string>("set");
        var fetchOptions = CoreFetchOptions.Default with { StaleTime = StaleTime };

        _clock.Advance(StaleTime - TimeSpan.FromTicks(1));
        Assert.False(state.IsStale(fetchOptions));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(state.IsStale(fetchOptions));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Set_Data_Younger_Than_StaleTime_Is_Served_Without_A_Fetch()
    {
        using var client = NewClient();
        client.SetQueryData(Options(), "set-data");
        _clock.Advance(StaleTime - TimeSpan.FromTicks(1));

        var data = await client.QueryAsync(Options(), TestContext.Current.CancellationToken)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("set-data", data);
        Assert.Equal(0, _calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_UpdatedAt_Moves_The_Timestamp()
    {
        using var client = NewClient();
        _clock.Advance(TimeSpan.FromHours(1));
        var obtained = _clock.GetUtcNow() - StaleTime + TimeSpan.FromTicks(1);

        client.SetQueryData(Options(), "older", updatedAt: obtained);

        var state = client.StateOf<string>("set");
        var fetchOptions = CoreFetchOptions.Default with { StaleTime = StaleTime };
        Assert.Equal(obtained, state.LastUpdatedAt);
        Assert.False(state.IsStale(fetchOptions));
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(state.IsStale(fetchOptions));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void An_UpdatedAt_Is_Honoured_By_The_Updater_Overload()
    {
        using var client = NewClient();
        _clock.Advance(TimeSpan.FromHours(1));
        var obtained = _clock.GetUtcNow() - TimeSpan.FromMinutes(2);

        client.SetQueryData(Options(), _ => "older", obtained);

        Assert.Equal(obtained, client.StateOf<string>("set").LastUpdatedAt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Static_Query_With_Set_Data_Stays_Fresh_Through_Invalidate()
    {
        using var client = NewClient();
        client.SetQueryData(Options(neverStale: true), "seeded");
        using var observer = client.Subscribe(Options(neverStale: true), () => { });

        client.Invalidate("set");

        Assert.Equal(0, _calls.Value);
        Assert.Equal("seeded", observer.Query.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Set_Never_Cancels_An_In_Flight_Fetch()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("set", handler.Handle).Build();
        using var observer = client.Subscribe(options, () => { });
        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);

        client.SetQueryData(options, "optimistic");

        Assert.False(handler.Token(0).IsCancellationRequested);
        Assert.True(observer.Query.IsFetching);
        Assert.Equal("optimistic", observer.Query.Data);
        Assert.Equal(1, handler.Calls);

        handler.Release(0, "landed");
        await Signals.UntilAsync(observer.Query, q => q.Data == "landed", TestContext.Current.CancellationToken)
            .WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Set_Made_While_A_Restore_Reads_The_Store_Refuses_The_Stored_Copy()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var options = Options();
        var state = client.GetOrCreateState(options);
        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);

        client.SetQueryData(options, "set-wins");
        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(_clock.GetUtcNow())));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(state.IsRestoring);
        Assert.Equal("set-wins", state.Data);
        Assert.Equal(0, _calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Restore_Still_Decides_What_Follows_After_A_Set_Refused_Its_Data()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var options = Options(staleTime: TimeSpan.Zero);
        using var observer = client.Subscribe(options, () => { });
        var state = client.StateOf<string>("set");
        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(state.IsRestoring);

        client.SetQueryData(options, "set-wins");
        persistence.ReleaseLoad(new PersistedQuery<string>("from-disk", new QueryFreshness(_clock.GetUtcNow())));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);
        await Signals.UntilAsync(state, q => q.Data == "fetched", TestContext.Current.CancellationToken)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(state.IsRestoring);
        Assert.Equal(1, _calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Set_Is_Saved_With_The_Freshness_It_Recorded()
    {
        using var persistence = new GatedPersistence();
        using var client = NewClient(persistence);
        var updatedAt = _clock.GetUtcNow().AddHours(-1);

        client.SetQueryData(Options(), "seeded", updatedAt);
        var save = await persistence.Saved(0).WaitAsync(TestContext.Current.CancellationToken);

        var entry = Assert.IsType<PersistedQuery<string>>(save.Entry);
        Assert.Equal("seeded", entry.Data);
        Assert.Equal(updatedAt, entry.Freshness.LastUpdatedAt);
        Assert.False(entry.Freshness.IsInvalidated);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Second_Set_Is_Saved_After_The_First_Even_While_The_First_Is_Held()
    {
        using var persistence = new GatedPersistence();
        persistence.HoldSaves();
        using var client = NewClient(persistence);
        var ct = TestContext.Current.CancellationToken;

        client.SetQueryData(Options(), "optimistic");
        await persistence.SaveStarted(0).WaitAsync(ct);
        client.SetQueryData(Options(), "rolled-back");

        Assert.Equal("rolled-back", client.FindQuery<string>("set")!.Data);
        Assert.Empty(persistence.Saves);

        persistence.ReleaseSave();
        await persistence.Saved(0).WaitAsync(ct);
        persistence.ReleaseSave();
        await persistence.Saved(1).WaitAsync(ct);

        Assert.Equal(
            ["optimistic", "rolled-back"],
            persistence.Saves.Select(s => ((PersistedQuery<string>)s.Entry).Data));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Set_Does_Not_Wait_On_The_Store()
    {
        using var persistence = new GatedPersistence();
        persistence.HoldSaves();
        using var client = NewClient(persistence);

        client.SetQueryData(Options(), "now");

        Assert.Equal("now", client.FindQuery<string>("set")!.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Failed_Save_Is_Swallowed_The_Set_Stands_And_The_Queue_Goes_On()
    {
        using var persistence = new GatedPersistence();
        persistence.FailNextSave(new IOException("disk full"));
        using var client = NewClient(persistence);
        var ct = TestContext.Current.CancellationToken;

        client.SetQueryData(Options(), "kept");
        client.SetQueryData(Options(), "later");
        var save = await persistence.Saved(0).WaitAsync(ct);

        Assert.Equal("later", ((PersistedQuery<string>)save.Entry).Data);
        var state = client.FindQuery<string>("set")!;
        Assert.Equal("later", state.Data);
        Assert.True(state.IsResolved);
    }
}
