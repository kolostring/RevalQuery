using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class RetryBackoffTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly FakeTimeProvider _clock = new();
    private readonly Counter _calls = new();

    public RetryBackoffTests()
    {
        SynchronizationContext.SetSynchronizationContext(null);
    }

    [Fact]
    public async Task The_Default_Schedule_Retries_After_Two_Four_And_Eight_Seconds()
    {
        using var client = NewClient();
        var fetch = client.QueryAsync(FailingQuery(r => r.Retry(3)));

        Assert.Equal(1, _calls.Value);
        AssertRetriesOnlyAt(TimeSpan.FromSeconds(2), expectedCalls: 2);
        AssertRetriesOnlyAt(TimeSpan.FromSeconds(4), expectedCalls: 3);
        AssertRetriesOnlyAt(TimeSpan.FromSeconds(8), expectedCalls: 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetch).WaitAsync(HangGuard);
    }

    [Fact]
    public async Task A_Custom_Delay_Sets_The_Wait_Before_Each_Retry()
    {
        using var client = NewClient();
        var fetch = client.QueryAsync(FailingQuery(r => r.Retry(2, attempt => TimeSpan.FromSeconds(10 * attempt))));

        Assert.Equal(1, _calls.Value);
        AssertRetriesOnlyAt(TimeSpan.FromSeconds(10), expectedCalls: 2);
        AssertRetriesOnlyAt(TimeSpan.FromSeconds(20), expectedCalls: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetch).WaitAsync(HangGuard);
    }

    [Fact]
    public async Task Cancelling_During_A_Backoff_Wait_Stops_Retrying_At_Once()
    {
        var policy = new ExponentialBackoffRetryPolicy(_clock);
        using var cts = new CancellationTokenSource();

        var run = policy.ExecuteWithRetryAsync<string>(
            () =>
            {
                _calls.Increment();
                return Task.FromException<string>(new InvalidOperationException("boom"));
            },
            new CoreRetryOptions(3, _ => TimeSpan.FromSeconds(5)),
            cts.Token);

        Assert.Equal(1, _calls.Value);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(HangGuard);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, _calls.Value);
    }

    private void AssertRetriesOnlyAt(TimeSpan delay, int expectedCalls)
    {
        _clock.Advance(delay - Tick);
        Assert.Equal(expectedCalls - 1, _calls.Value);

        _clock.Advance(Tick);
        Assert.Equal(expectedCalls, _calls.Value);
    }

    private RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(),
            new RevalQueryOptions { TimeProvider = _clock });

    private QueryOptions<ValueTuple<string>, string> FailingQuery(Action<RetryOptionsBuilder> retry) =>
        QueryOptions.Create("backoff", _ =>
            {
                _calls.Increment();
                return Task.FromException<string>(new InvalidOperationException("boom"));
            })
            .ConfigureRetry(r => retry(r))
            .Build();
}
