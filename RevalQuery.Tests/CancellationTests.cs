using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// What cancelling a query means: the fetch stops, its result is dropped even when the handler
/// never read its token, and the caller knows when both have happened.
/// </summary>
public class CancellationTests
{
    [Fact]
    public async Task A_Handler_That_Ignored_Its_Token_Has_Its_Result_Dropped()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        // No token anywhere in the handler, which is the case this test exists for. A query
        // written like this used to apply its result long after the cancellation, because
        // cancelling only signalled a token nothing was reading.
        var options = QueryOptions.Create<string>("ignored", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        // The low-level trigger, which returns without waiting, so the write below happens
        // while the handler is still running. That is the race: the handler finishes later and
        // its result would land on top.
        observer.Query.Cancel();
        observer.Query.Data = "optimistic";

        // Waited for rather than slept past. The handler has no token to observe, so it runs to
        // its own end and the query goes idle only then; how long that takes depends on the
        // machine, and the point of the test is what it does when it gets there.
        await TestUtils.WaitUntilAsync(() => observer.Query.IsIdle);

        // Given a moment to land on top, which is what the defect did.
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

        // No delay and no polling in between. The moment the await returns, the fetch is over
        // and nothing it produced can still land, which is what makes this usable before an
        // optimistic update.
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

        // The prefix, not either key. CancelAsync covers the same set as Invalidate, so the
        // two can be used on the same key.
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

        // The caller walked away; the fetch did not. Cancelling it too would throw away the
        // cache entry that makes the next keystroke, or the backspace after it, instant.
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

        // The failure is still on the query, because nothing clears it until a fetch succeeds.
        Assert.True(client.FindQuery<string>("stale-error")!.IsException);

        var second = client.QueryAsync(options);
        await secondStarted.Task;
        await client.CancelAsync("stale-error");

        // This call was cancelled. Reporting the earlier call's exception would tell the
        // caller about an error its own fetch never hit.
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

        // A component watching the key, disabled so that it does not fetch on its own. Built
        // from its own options because a query captures the handler of whichever options
        // created it, so both must carry the same one.
        var observer = client.Subscribe(
            QueryOptions.Create<string>("unmounted", Options(started, release).Handler)
                .Enabled(false).Build(),
            () => { });

        var fetch = client.QueryAsync(Options(started, release));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The component unmounts. Its going away releases the worker, which used to cancel
        // whatever that worker had in flight: a fetch this component never asked for and is
        // not the one walking away from.
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

            // An aborted request surfacing as the handler's own exception type rather than an
            // OperationCanceledException, which is what an HTTP client does with a socket the
            // cancellation tore down.
            throw new IOException("socket aborted");
        }).ConfigureRetry(retry => retry.Retry(0)).Build();

        var fetch = client.QueryAsync(options);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = client.CancelAsync("aborted");
        release.TrySetResult();
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        // CancelAsync promises a cancelled query keeps its data and records no error. Recording
        // one here also marked the query settled, which permanently blocked any later restore.
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

        // Arrives after the cancellation was requested and before the first fetch has finished
        // unwinding. Joining that fetch would report it cancelled, which is an answer about a
        // request made before this one existed.
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

        // The scope owning the client is ending, so the handler is about to reach for services
        // that are going away and nobody is left to receive what it produces. This is the one
        // teardown that does cancel.
        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetch.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---------- helpers ----------

    private static QueryClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
}
