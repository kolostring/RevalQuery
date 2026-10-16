using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Tests;

public class ReviewOf1784595Tests
{
    [Fact]
    public async Task Stopping_The_Collector_After_Disposing_It_Is_Harmless()
    {
        var collector = new TtlQueryGarbageCollector(new RevalQueryOptions());

        collector.RegisterForEviction(ValueTuple.Create("gc"), null);

        collector.Dispose();
        collector.Dispose();

        await collector.StopAsync();
        await collector.DisposeAsync();
    }

    [Fact]
    public async Task A_Negative_Retry_Delay_Does_Not_Replace_The_Handler_Failure()
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteWithRetryAsync<string>(
                () =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("the real failure");
                },
                new CoreRetryOptions(2, _ => TimeSpan.FromSeconds(-1))));

        Assert.Equal("the real failure", thrown.Message);
        Assert.Equal(3, calls);
    }
}
