using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class ReviewOf45161b7Tests
{
    [Fact]
    public async Task A_Static_QueryAsync_Does_Not_Freeze_The_Key_For_Later_Subscribers()
    {
        var calls = 0;

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        static QueryOptions<ValueTuple<string>, string> Build(Action onCall, bool neverStale)
        {
            var builder = QueryOptions.Create<string>("frozen", _ =>
            {
                onCall();
                return Task.FromResult("data");
            });

            builder.ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)));

            if (neverStale) builder.ConfigureFetch(f => f.NeverStale());

            return builder.Build();
        }

        await client.QueryAsync(Build(() => Interlocked.Increment(ref calls), neverStale: true));
        Assert.Equal(1, calls);

        using var observer = client.Subscribe(Build(() => Interlocked.Increment(ref calls), neverStale: false), () => { });

        client.Invalidate("frozen");

        await TestUtils.WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidating_A_Static_Query_That_Never_Produced_Data_Refetches()
    {
        var calls = 0;

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("retryable", _ =>
            {
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

    [Fact]
    public async Task A_Cache_Hit_Still_Honours_A_Cancelled_Token()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("warm", static _ => Task.FromResult("data"))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        Assert.Equal("data", await client.QueryAsync(options));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryAsync(options, cts.Token));
    }
}
