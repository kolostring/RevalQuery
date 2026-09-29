using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Tests;

/// <summary>
/// Retry counts retries, not attempts: a count of n means n further attempts after the first,
/// so n + 1 calls in all and zero still calls the handler once.
/// </summary>
public class RetryCountTests
{
    private static CoreRetryOptions Retries(int count) =>
        new(count, _ => TimeSpan.Zero);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task A_Failing_Handler_Is_Called_Retry_Plus_One_Times(int retries, int expectedCalls)
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteWithRetryAsync<string>(() =>
            {
                calls++;
                return Task.FromException<string>(new InvalidOperationException("boom"));
            }, Retries(retries)));

        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task Zero_Retries_Surfaces_The_Handler_Exception_Rather_Than_Inventing_One()
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var thrown = new InvalidOperationException("the real error");

        // The old shape fell out of the loop with nothing to return and threw
        // "Retry policy failed to return result", discarding what actually went wrong.
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteWithRetryAsync<string>(() => Task.FromException<string>(thrown), Retries(0)));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public async Task A_Handler_That_Recovers_Stops_Retrying()
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var calls = 0;

        var result = await policy.ExecuteWithRetryAsync(() =>
        {
            calls++;
            return calls < 3
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("value");
        }, Retries(5));

        Assert.Equal("value", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Cancellation_Stops_Retrying_And_Surfaces_The_Failure()
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var calls = 0;
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            policy.ExecuteWithRetryAsync<string>(() =>
            {
                calls++;
                cts.Cancel();
                return Task.FromException<string>(new InvalidOperationException("boom"));
            }, Retries(5), cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Queries_Default_To_Three_Retries_And_Mutations_To_None()
    {
        Assert.Equal(3, CoreRetryOptions.QueryDefault.Retry);
        Assert.Equal(0, CoreRetryOptions.MutationDefault.Retry);
    }
}
