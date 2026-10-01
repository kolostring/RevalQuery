using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// Findings from reviewing a700df3, the concurrency commit that went in after the review pass
/// and was never itself reviewed.
/// </summary>
public class ReviewOfA700df3Tests
{
    [Fact]
    public async Task A_Cancelled_Fetch_Does_Not_Return_Data_It_Never_Fetched()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create("cancelled", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(5000, ctx.CancellationToken ?? CancellationToken.None);
            return "value";
        }).Build();

        var fetch = client.QueryAsync(options);
        await started.Task;
        await client.CancelAsync("cancelled");

        // The handler never produced anything. Returning null as though it had is worse than
        // saying so: the caller cannot tell the difference between "no data" and "not fetched".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task A_Restore_Does_Not_Clear_A_Failed_Query()
    {
        var persistence = new SlowPersistence("from-disk", loadDelayMs: 150);
        using var client = NewClient(persistence);

        // No retries, so the failure is recorded well before the load lands. The default of
        // 3 retries backs off for longer than the load takes, which would hide the ordering
        // this test is about.
        var options = QueryOptions.Create<string>(
            "failed", _ => Task.FromException<string>(new InvalidOperationException("boom")))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(options));

        var state = client.FindQuery<string>("failed")!;
        Assert.True(state.IsException);

        // The load lands after the failure. Adopting it would leave Status resolved with
        // Exception still set, so a component branching on Exception renders an error next to
        // data, and a later QueryAsync returns stale data instead of throwing.
        await Task.Delay(250);

        Assert.True(state.IsException);
        Assert.NotNull(state.Exception);
        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task A_Successful_Fetch_Clears_An_Earlier_Failure()
    {
        using var client = NewClient();

        var shouldFail = true;

        var options = QueryOptions.Create("recovering", _ => shouldFail
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("value"))
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(options));

        shouldFail = false;
        Assert.Equal("value", await client.QueryAsync(options));

        // Resolved with a stale exception still hanging off it is the same broken shape a
        // restore over a failure produces: a component checking Exception renders an error
        // beside perfectly good data.
        var state = client.FindQuery<string>("recovering")!;
        Assert.True(state.IsResolved);
        Assert.Null(state.Exception);
    }

    [Fact]
    public async Task A_Failed_Fetch_Still_Throws_When_A_Restore_Lands_During_It()
    {
        var persistence = new SlowPersistence("from-disk", loadDelayMs: 30);
        using var client = NewClient(persistence);

        // No retries: the restore landing mid-fetch is what this test is about, and backing
        // off through three of them only makes it slower.
        var options = QueryOptions.Create<string>("racing", async _ =>
        {
            await Task.Delay(80);
            throw new InvalidOperationException("boom");
        }).ConfigureRetry(r => r.Retry(0)).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(options));
    }

    private static QueryClient NewClient(IQueryPersistence? persistence = null) =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);

    private sealed class SlowPersistence(string stored, int loadDelayMs) : IQueryPersistence
    {
        public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, CancellationToken ct = default)
        {
            await Task.Delay(loadDelayMs, ct);
            return (PersistedQuery<TRes>?)(object)new PersistedQuery<string>(stored, new QueryFreshness(DateTimeOffset.UtcNow));
        }

        public ValueTask SaveAsync<TRes>(
            System.Runtime.CompilerServices.ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) =>
            default;
    }
}
