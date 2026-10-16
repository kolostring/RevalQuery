using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class ReviewOf45161b7Tests
{
    [Fact]
    public async Task A_Static_QueryAsync_Does_Not_Freeze_The_Key_For_Later_Subscribers()
    {
        var calls = new Counter();

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

        await client.QueryAsync(Build(() => calls.Increment(), neverStale: true));
        Assert.Equal(1, calls.Value);

        using var observer = client.Subscribe(Build(() => calls.Increment(), neverStale: false), () => { });
        Assert.Equal(1, calls.Value);

        client.Invalidate("frozen");

        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public void Invalidating_A_Static_Query_That_Never_Produced_Data_Refetches()
    {
        var calls = new Counter();

        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new RevalClient(sp, new RevalQueryOptions());

        var options = QueryOptions.Create<string>("retryable", _ =>
            {
                if (calls.Increment() == 1) throw new InvalidOperationException("boom");

                return Task.FromResult("recovered");
            })
            .ConfigureFetch(f => f.NeverStale())
            .ConfigureRetry(r => r.Retry(0))
            .Build();

        using var observer = client.Subscribe(options, () => { });

        Assert.NotNull(observer.Query.Exception);
        Assert.Equal(1, calls.Value);

        client.Invalidate("retryable");

        Assert.Equal("recovered", observer.Query.Data);
        Assert.Equal(2, calls.Value);
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
