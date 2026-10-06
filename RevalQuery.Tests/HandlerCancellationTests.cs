using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class HandlerCancellationTests
{
    private sealed record Req(string Name);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    [Fact]
    public async Task A_Query_Handler_Throwing_TaskCanceledException_Is_An_Error()
    {
        using var client = NewClient();
        var calls = 0;

        var options = QueryOptions.Create<string>("timeout", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<string>(new TaskCanceledException("timed out"));
        }).ConfigureRetry(retry => retry.Retry(2)).Build();

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.QueryAsync(options));

        var query = client.FindQuery<string>("timeout")!;
        Assert.True(query.IsException);
        Assert.False(query.IsFetching);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_Cancelled_Query_Is_Not_An_Error()
    {
        using var client = NewClient();
        var started = Signal();

        var options = QueryOptions.Create<string>("genuine", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken ?? CancellationToken.None);
            return "unreachable";
        }).ConfigureRetry(retry => retry.Retry(0)).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        await client.CancelAsync("genuine");

        Assert.True(observer.Query.IsIdle);
        Assert.False(observer.Query.IsException);
        Assert.Null(observer.Query.Data);
    }

    [Fact]
    public async Task A_Mutation_Handler_Throwing_TaskCanceledException_Is_An_Error()
    {
        using var client = NewClient();
        var exceptions = 0;
        var settled = 0;

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(_ => Task.FromException<string>(new TaskCanceledException("timed out")))
                .ConfigureRetry(retry => retry.Retry(0))
                .OnException((_, _) => { exceptions++; return Task.CompletedTask; })
                .OnSettled((_, _, _) => { settled++; return Task.CompletedTask; })
                .Build(),
            () => { });

        await observer.State.ExecuteAsync(new Req("a"));

        Assert.Equal(MutationStatus.Exception, observer.State.Status);
        Assert.IsType<TaskCanceledException>(observer.State.Exception);
        Assert.Equal(1, exceptions);
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task A_Mutation_Cancelled_By_The_Callers_Token_Restores_The_Status_And_Throws()
    {
        using var client = NewClient();
        var started = Signal();
        var settled = 0;
        var notifications = 0;

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(async ctx =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                    return "unreachable";
                })
                .OnSettled((_, _, _) => { settled++; return Task.CompletedTask; })
                .Build(),
            () => notifications++);

        using var cts = new CancellationTokenSource();
        var run = observer.State.ExecuteAsync(new Req("a"), cts.Token);
        await started.Task;
        Assert.Equal(MutationStatus.Fetching, observer.State.Status);

        var before = notifications;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(MutationStatus.Idle, observer.State.Status);
        Assert.Equal(0, settled);
        Assert.True(notifications > before);
    }

    [Fact]
    public async Task Cancelling_The_Latest_Run_While_An_Older_One_Is_In_Flight_Does_Not_Stay_Fetching()
    {
        using var client = NewClient();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = Signal();

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(async ctx =>
                {
                    if (ctx.Params.Name == "first") return await first.Task;
                    secondStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                    return "unreachable";
                })
                .Build(),
            () => { });

        var older = observer.State.ExecuteAsync(new Req("first"));

        using var cts = new CancellationTokenSource();
        var latest = observer.State.ExecuteAsync(new Req("second"), cts.Token);
        await secondStarted.Task;

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => latest);

        first.SetResult("stale");
        await older;

        Assert.NotEqual(MutationStatus.Fetching, observer.State.Status);
    }
}
