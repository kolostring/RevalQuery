using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class LoadingDuringRestoreTests
{
    private sealed class GatedPersistence(PersistedQuery<string>? entry) : IQueryPersistence
    {
        public readonly SemaphoreSlim LoadGate = new(0);
        public readonly TaskCompletionSource LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
        {
            LoadStarted.TrySetResult();
            await LoadGate.WaitAsync(ct);
            return (PersistedQuery<TRes>?)(object?)entry;
        }

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_Query_Waiting_On_A_Restore_Is_Loading_But_Not_Fetching()
    {
        var persistence = new GatedPersistence(
            new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)));

        using var client = NewClient(persistence);

        var handlerGate = new TaskCompletionSource();
        var options = QueryOptions.Create<string>("restoring", async _ =>
            {
                await handlerGate.Task;
                return "from-network";
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        var observer = client.Subscribe(options, () => { });
        var state = observer.Query;

        await persistence.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(state.IsRestoring);
        Assert.True(state.IsLoading);
        Assert.False(state.IsFetching);
        Assert.True(state.IsPending);

        Assert.True(state.CanFetch);

        persistence.LoadGate.Release();
        handlerGate.TrySetResult();

        await TestUtils.WaitForStateAsync(state, s => !s.IsRestoring);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public async Task A_Restore_That_Finds_Nothing_Stops_Loading_And_Notifies()
    {
        var persistence = new GatedPersistence(null);
        using var client = NewClient(persistence);

        var options = QueryOptions.Create<string>("empty", _ => Task.FromResult("from-network"))
            .Enabled(false)
            .Build();

        var changes = 0;
        var observer = client.Subscribe(options, () => Interlocked.Increment(ref changes));
        var state = observer.Query;

        await persistence.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(state.IsLoading);

        persistence.LoadGate.Release();

        await TestUtils.WaitForStateAsync(state, s => !s.IsLoading);
        Assert.False(state.IsRestoring);
        Assert.True(state.IsPending);

        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref changes) > 0);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task A_Query_Without_Persistence_Is_Never_Restoring()
    {
        using var client = NewClient(persistence: null);

        var options = QueryOptions.Create<string>("plain", _ => Task.FromResult("value")).Build();
        var observer = client.Subscribe(options, () => { });

        Assert.False(observer.Query.IsRestoring);

        await TestUtils.WaitForStateAsync(observer.Query, s => s.IsResolved);
        Assert.False(observer.Query.IsLoading);
    }

    private static RevalClient NewClient(IQueryPersistence? persistence) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);
}
