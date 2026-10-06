using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class CancellationTests
{
    [Fact]
    public async Task A_Handler_That_Ignored_Its_Token_Has_Its_Result_Dropped()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("ignored", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        observer.Query.Cancel();
        observer.Query.Data = "optimistic";

        await TestUtils.WaitUntilAsync(() => observer.Query.IsIdle);

        await Task.Delay(100);

        Assert.Equal("optimistic", observer.Query.Data);
        Assert.False(observer.Query.IsException);
    }

    [Fact]
    public async Task CancelAsync_Completes_Only_Once_The_Fetch_Has_Unwound()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("unwind", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        await client.CancelAsync("unwind");

        Assert.True(observer.Query.IsIdle);
        Assert.Null(observer.Query.Data);
    }

    [Fact]
    public async Task CancelAsync_Cancels_Every_Query_Under_The_Prefix()
    {
        using var client = NewClient();

        var started = new CountdownEvent(2);

        static QueryOptions<(string, string), string> Options(string id, CountdownEvent started) =>
            QueryOptions.Create<(string, string), string>(("users", id), async _ =>
            {
                started.Signal();
                await Task.Delay(200);
                return "from-network";
            }).Build();

        var first = client.Subscribe(Options("a", started), () => { });
        var second = client.Subscribe(Options("b", started), () => { });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        await client.CancelAsync(ValueTuple.Create("users"));

        Assert.True(first.Query.IsIdle);
        Assert.True(second.Query.IsIdle);
        Assert.Null(first.Query.Data);
        Assert.Null(second.Query.Data);
    }

    [Fact]
    public async Task A_Cancelled_Await_Abandons_The_Wait_And_Keeps_The_Fetch()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("abandoned", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        using var cts = new CancellationTokenSource();
        var fetch = client.QueryAsync(options, cts.Token);

        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);

        await TestUtils.WaitUntilAsync(() => client.FindQuery<string>("abandoned")?.Data is not null);
        Assert.Equal("from-network", client.FindQuery<string>("abandoned")!.Data);
    }

    [Fact]
    public async Task A_Cancelled_Fetch_Does_Not_Throw_An_Older_Fetch_Failure()
    {
        using var client = NewClient();

        var calls = 0;
        var secondStarted = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("stale-error", async ctx =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("first");

            secondStarted.TrySetResult();
            await Task.Delay(5000, ctx.CancellationToken ?? CancellationToken.None);
            return "from-network";
        }).ConfigureRetry(retry => retry.Retry(0)).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(options));

        Assert.True(client.FindQuery<string>("stale-error")!.IsException);

        var second = client.QueryAsync(options);
        await secondStarted.Task;
        await client.CancelAsync("stale-error");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task Unsubscribing_Does_Not_Cancel_A_Fetch_Someone_Else_Is_Awaiting()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        static QueryOptions<ValueTuple<string>, string> Options(
            TaskCompletionSource started, TaskCompletionSource release) =>
            QueryOptions.Create<string>("unmounted", async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return "from-network";
            }).Build();

        var observer = client.Subscribe(
            QueryOptions.Create<string>("unmounted", Options(started, release).Handler)
                .Enabled(false).Build(),
            () => { });

        var fetch = client.QueryAsync(Options(started, release));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Dispose();

        release.TrySetResult();

        Assert.Equal("from-network", await fetch.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_Handler_Failing_While_Cancelled_Records_No_Error()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("aborted", async _ =>
        {
            started.TrySetResult();
            await release.Task;

            throw new IOException("socket aborted");
        }).ConfigureRetry(retry => retry.Retry(0)).Build();

        var fetch = client.QueryAsync(options);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = client.CancelAsync("aborted");
        release.TrySetResult();
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        var state = client.FindQuery<string>("aborted")!;
        Assert.False(state.IsException);
        Assert.Null(state.Exception);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task A_Fetch_Requested_After_A_Cancel_Does_Not_Inherit_It()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;

        var options = QueryOptions.Create<string>("superseded", async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task;
            }

            return "from-network";
        }).Build();

        var first = client.QueryAsync(options);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = client.CancelAsync("superseded");

        var second = client.QueryAsync(options);

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("from-network", await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Disposing_The_Client_Cancels_The_Fetches_It_Was_Driving()
    {
        var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("torn-down", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(5000, ctx.CancellationToken ?? CancellationToken.None);
            return "from-network";
        }).Build();

        var fetch = client.QueryAsync(options);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetch.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
}
