using Microsoft.Extensions.Time.Testing;
using static RevalQuery.TestKit.Kit;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Tests;

public class ReviewOf1784595Tests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Stopping_The_Collector_After_Disposing_It_Waits_For_The_Running_Sweep()
    {
        var time = new FakeTimeProvider();
        var collector = new TtlQueryGarbageCollector(new RevalQueryOptions
        {
            TimeProvider = time,
            CacheOptions = new CoreCacheOptions(GcTime: TimeSpan.Zero, GcInterval: TimeSpan.FromSeconds(1))
        });
        var sweepStarted = new Gate();
        var releaseSweep = new Gate();
        collector.OnEvictionRequired += _ =>
        {
            sweepStarted.Open();
            releaseSweep.Task.Wait(TestContext.Current.CancellationToken);
        };
        collector.RegisterForEviction(ValueTuple.Create("gc"), null);

        var sweep = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        await sweepStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        collector.Dispose();
        var stop = collector.StopAsync();

        Assert.False(stop.IsCompleted);

        releaseSweep.Open();
        await stop.WaitAsync(TestContext.Current.CancellationToken);
        await sweep.WaitAsync(TestContext.Current.CancellationToken);

        collector.Dispose();
        await collector.StopAsync();
        await collector.DisposeAsync();
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Disposing_The_Collector_Async_After_Disposing_It_Waits_For_The_Running_Sweep()
    {
        var time = new FakeTimeProvider();
        var collector = new TtlQueryGarbageCollector(new RevalQueryOptions
        {
            TimeProvider = time,
            CacheOptions = new CoreCacheOptions(GcTime: TimeSpan.Zero, GcInterval: TimeSpan.FromSeconds(1))
        });
        var sweepStarted = new Gate();
        var releaseSweep = new Gate();
        collector.OnEvictionRequired += _ =>
        {
            sweepStarted.Open();
            releaseSweep.Task.Wait(TestContext.Current.CancellationToken);
        };
        collector.RegisterForEviction(ValueTuple.Create("gc"), null);

        var sweep = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        await sweepStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        collector.Dispose();
        var stop = collector.DisposeAsync().AsTask();

        Assert.False(stop.IsCompleted);

        releaseSweep.Open();
        await stop.WaitAsync(TestContext.Current.CancellationToken);
        await sweep.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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
                new CoreRetryOptions(2, _ => TimeSpan.FromSeconds(-1)), TestContext.Current.CancellationToken));

        Assert.Equal("the real failure", thrown.Message);
        Assert.Equal(3, calls);
    }
}
