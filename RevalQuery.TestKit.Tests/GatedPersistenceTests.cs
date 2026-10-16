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

    [Fact]
    public async Task A_Load_Waits_Until_It_Is_Released_With_An_Entry()
    {
        using var persistence = new GatedPersistence();
        var entry = Entry("stored");

        var load = persistence.LoadAsync<string>(Key).AsTask();
        await persistence.LoadStarted.WaitAsync(HangGuard);
        Assert.False(load.IsCompleted);

        persistence.ReleaseLoad(entry);

        Assert.Same(entry, await load.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task A_Load_Released_With_Nothing_Returns_Null()
    {
        using var persistence = new GatedPersistence();
        var load = persistence.LoadAsync<string>(Key).AsTask();

        persistence.ReleaseLoad();

        Assert.Null(await load.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Releases_Go_To_Loads_In_The_Order_They_Started_Including_Loads_Still_To_Come()
    {
        using var persistence = new GatedPersistence();
        var first = persistence.LoadAsync<string>(Key).AsTask();
        persistence.ReleaseLoad(Entry("first"));
        persistence.ReleaseLoad(Entry("second"));

        var second = persistence.LoadAsync<string>(Key).AsTask();

        Assert.Equal("first", (await first.WaitAsync(HangGuard))!.Data);
        Assert.Equal("second", (await second.WaitAsync(HangGuard))!.Data);
        Assert.Equal(2, persistence.Loads);
    }

    [Fact]
    public async Task Saves_Are_Recorded_And_Awaitable()
    {
        using var persistence = new GatedPersistence();
        var entry = Entry("saved");
        var saved = persistence.Saved(0);
        Assert.False(saved.IsCompleted);

        await persistence.SaveAsync(Key, entry);

        var save = await saved.WaitAsync(HangGuard);
        Assert.Equal(Key, save.Key);
        Assert.Same(entry, save.Entry);
        Assert.Equal([save], persistence.Saves);
    }

    [Fact]
    public async Task Disposing_Releases_Pending_And_Later_Loads_With_Nothing()
    {
        var persistence = new GatedPersistence();
        var pending = persistence.LoadAsync<string>(Key).AsTask();

        persistence.Dispose();

        Assert.Null(await pending.WaitAsync(HangGuard));
        Assert.Null(await persistence.LoadAsync<string>(Key).AsTask().WaitAsync(HangGuard));
    }

    [Fact]
    public async Task A_Query_Restores_What_The_Test_Releases()
    {
        using var persistence = new GatedPersistence();
        var handler = new GatedHandler<string>();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), new ManualEviction(), persistence);
        client.Subscribe(QueryOptions.Create("a", handler.Handle).ConfigureFetch(f => f.NeverStale()).Build(), () => { });
        var state = client.FindQuery<string>("a")!;

        await persistence.LoadStarted.WaitAsync(HangGuard);
        Assert.True(state.IsRestoring);

        persistence.ReleaseLoad(Entry("stored"));
        await state.RestoreCompleted.WaitAsync(HangGuard);

        Assert.Equal("stored", state.Data);
        Assert.Equal(0, handler.Calls);
    }
}
