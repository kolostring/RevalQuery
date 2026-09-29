using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class InvalidateDuringRestoreTests
{
    /// <summary>
    /// A persistence adapter whose load blocks until the test releases it.
    /// </summary>
    private sealed class GatedPersistence(PersistedQuery<string> entry) : IQueryPersistence
    {
        public readonly SemaphoreSlim LoadGate = new(0);
        public readonly TaskCompletionSource LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
        {
            LoadStarted.TrySetResult();
            await LoadGate.WaitAsync(ct);
            return (PersistedQuery<TRes>)(object)entry;
        }

        public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches()
    {
        var handlerCalls = 0;

        // Stored data is fresh, and StaleTime is long, so after the restore the query is
        // NOT stale. Only the invalidation should be able to force a fetch.
        var persistence = new GatedPersistence(new PersistedQuery<string>("from-disk", DateTimeOffset.UtcNow));

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions(), persistence: persistence);

        var options = QueryOptions.Create<string>("gated", _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        var observer = client.Subscribe(options, () => { });

        await persistence.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Invalidate while the load is still outstanding.
        client.Invalidate("gated");

        persistence.LoadGate.Release();

        await Task.Delay(400);

        // The invalidation fires a fetch straight away, because CanFetch is true while the
        // restore is still outstanding. TryRestore then declines, since _hasFetched is set.
        //
        // This is load-bearing: anything that makes the query report FetchStatus.Fetching
        // for the duration of the restore turns CanFetch false, drops the invalidation,
        // and lets the stale restored value win. Keep this test if that changes.
        Assert.True(handlerCalls > 0,
            $"invalidation was lost across the restore; handler ran {handlerCalls} times");
        Assert.Equal("from-network", observer.Query.Data);
    }
}
