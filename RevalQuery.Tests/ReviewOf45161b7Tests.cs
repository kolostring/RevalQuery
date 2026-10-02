using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// Defects found reviewing 45161b7, the commit that collapsed the imperative API into
/// <c>QueryAsync</c>.
/// </summary>
/// <remarks>
/// The first two share a failure mode and it is the one the commit was written to remove: a
/// query that can never be refreshed again. Each fails with its fix reverted.
/// </remarks>
public class ReviewOf45161b7Tests
{
    // A loader asking for NeverStale() must not decide freshness for the components that come
    // after it. Before the fix the query was created from the loader's options and a
    // subscriber only reached ApplyOptions on its second render, so the first one ran static.
    [Fact]
    public async Task A_Static_QueryAsync_Does_Not_Freeze_The_Key_For_Later_Subscribers()
    {
        var calls = 0;

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        static QueryOptions<ValueTuple<string>, string> Build(Action onCall, bool neverStale)
        {
            var builder = QueryOptions.Create<string>("frozen", _ =>
            {
                onCall();
                return Task.FromResult("data");
            });

            // Long enough that only the invalidation can force the second fetch.
            builder.ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)));

            if (neverStale) builder.ConfigureFetch(f => f.NeverStale());

            return builder.Build();
        }

        await client.QueryAsync(Build(() => Interlocked.Increment(ref calls), neverStale: true));
        Assert.Equal(1, calls);

        // Plain options, so this component wants ordinary invalidation to reach it.
        using var observer = client.Subscribe(Build(() => Interlocked.Increment(ref calls), neverStale: false), () => { });

        client.Invalidate("frozen");

        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(2, calls);
    }

    // A static query whose first fetch threw holds no data, which is stale on the first rung.
    // Invalidate has to reach it, or a retry button wired to it is dead for good.
    [Fact]
    public async Task Invalidating_A_Static_Query_That_Never_Produced_Data_Refetches()
    {
        var calls = 0;

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("retryable", _ =>
            {
                // Fails the first time only, so the retry is what the assertion measures.
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");

                return Task.FromResult("recovered");
            })
            .ConfigureFetch(f => f.NeverStale())
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        using var observer = client.Subscribe(options, () => { });

        await TestUtils.WaitUntilAsync(() => observer.Query.Exception is not null);
        Assert.Equal(1, calls);

        client.Invalidate("retryable");

        await TestUtils.WaitUntilAsync(() => observer.Query.Data is not null);
        Assert.Equal("recovered", observer.Query.Data);
    }

    // The cancellation contract cannot depend on whether the registry happened to be warm.
    [Fact]
    public async Task A_Cache_Hit_Still_Honours_A_Cancelled_Token()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("warm", static _ => Task.FromResult("data"))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        Assert.Equal("data", await client.QueryAsync(options));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Fresh data is sitting in the registry, so this never reaches a fetch.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryAsync(options, cts.Token));
    }
}
