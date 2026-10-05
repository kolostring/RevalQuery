using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class InvalidateDuringRestoreTests
{
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
    public async Task An_Invalidation_During_A_Restore_Survives_When_Nothing_Can_Fetch_Yet()
    {
        var handlerCalls = 0;

        var persistence = new GatedPersistence(new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)));

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions(), persistence: persistence);

        var options = QueryOptions.Create<string>("unobserved", _ =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        client.GetOrCreateQuery(options);

        await persistence.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.Invalidate("unobserved");

        persistence.LoadGate.Release();
        await client.FindQuery<string>("unobserved")!.RestoreCompleted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, handlerCalls);

        using var observer = client.Subscribe(options, () => { });

        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref handlerCalls) == 1);
        Assert.Equal("from-network", observer.Query.Data);
    }

    [Fact]
    public async Task Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches()
    {
        var handlerCalls = 0;

        var persistence = new GatedPersistence(new PersistedQuery<string>("from-disk", new QueryFreshness(DateTimeOffset.UtcNow)));

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

        client.Invalidate("gated");

        persistence.LoadGate.Release();

        await Task.Delay(400);

        Assert.True(handlerCalls > 0,
            $"invalidation was lost across the restore; handler ran {handlerCalls} times");
        Assert.Equal("from-network", observer.Query.Data);
    }
}
