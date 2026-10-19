using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.TestKit.Tests;

public class GatedPersistenceTests
{
    private static readonly ValueTuple<string> Key = ValueTuple.Create("a");

    private static PersistedQuery<string> Entry(string data) => new(data, new QueryFreshness(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Load_Waits_Until_It_Is_Released_With_An_Entry()
    {
        using var persistence = new GatedPersistence();
        var entry = Entry("stored");

        var load = persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask();
        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(load.IsCompleted);

        persistence.ReleaseLoad(entry);

        Assert.Same(entry, await load.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Load_Released_With_Nothing_Returns_Null()
    {
        using var persistence = new GatedPersistence();
        var load = persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask();

        persistence.ReleaseLoad();

        Assert.Null(await load.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Releases_Go_To_Loads_In_The_Order_They_Started_Including_Loads_Still_To_Come()
    {
        using var persistence = new GatedPersistence();
        var first = persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask();
        persistence.ReleaseLoad(Entry("first"));
        persistence.ReleaseLoad(Entry("second"));

        var second = persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask();

        Assert.Equal("first", (await first.WaitAsync(TestContext.Current.CancellationToken))!.Data);
        Assert.Equal("second", (await second.WaitAsync(TestContext.Current.CancellationToken))!.Data);
        Assert.Equal(2, persistence.Loads);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Saves_Are_Recorded_And_Awaitable()
    {
        using var persistence = new GatedPersistence();
        var entry = Entry("saved");
        var saved = persistence.Saved(0);
        Assert.False(saved.IsCompleted);

        await persistence.SaveAsync(Key, entry, TestContext.Current.CancellationToken);

        var save = await saved.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Key, save.Key);
        Assert.Same(entry, save.Entry);
        Assert.Equal([save], persistence.Saves);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Held_Save_Is_Recorded_Only_When_Released()
    {
        using var persistence = new GatedPersistence();
        persistence.HoldSaves();
        var ct = TestContext.Current.CancellationToken;

        var save = persistence.SaveAsync(Key, Entry("held"), ct).AsTask();
        await persistence.SaveStarted(0).WaitAsync(ct);

        Assert.False(save.IsCompleted);
        Assert.Empty(persistence.Saves);

        persistence.ReleaseSave();
        await save.WaitAsync(ct);

        Assert.Single(persistence.Saves);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Failed_Save_Throws_Once_And_Is_Not_Recorded()
    {
        using var persistence = new GatedPersistence();
        var ct = TestContext.Current.CancellationToken;
        persistence.FailNextSave(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => persistence.SaveAsync(Key, Entry("lost"), ct).AsTask());
        await persistence.SaveAsync(Key, Entry("kept"), ct);

        Assert.Single(persistence.Saves);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Disposing_Releases_Pending_And_Later_Loads_With_Nothing()
    {
        var persistence = new GatedPersistence();
        var pending = persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask();

        persistence.Dispose();

        Assert.Null(await pending.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Null(await persistence.LoadAsync<string>(Key, TestContext.Current.CancellationToken).AsTask().WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Query_Restores_What_The_Test_Releases()
    {
        using var persistence = new GatedPersistence();
        var handler = new GatedHandler<string>();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), new ManualEviction(), persistence);
        client.Subscribe(QueryOptions.Create("a", handler.Handle).ConfigureFetch(f => f.NeverStale()).Build(), () => { });
        var state = client.StateOf<string>("a");

        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(state.IsRestoring);

        persistence.ReleaseLoad(Entry("stored"));
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("stored", state.Data);
        Assert.Equal(0, handler.Calls);
    }
}
