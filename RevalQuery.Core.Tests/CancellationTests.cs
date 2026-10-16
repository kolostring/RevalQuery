using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class CancellationTests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Handler_That_Ignored_Its_Token_Has_Its_Result_Dropped()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>(honourToken: false);

        try
        {
            var observer = client.Subscribe(QueryOptions.Create("ignored", handler.Handle).Build(), () => { });
            Assert.Equal(1, handler.Calls);

            observer.Query.Cancel();
            observer.Query.Data = "optimistic";

            Assert.True(observer.Query.IsFetching);

            handler.Release(0, "from-network");
            await client.CancelAsync("ignored").WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("optimistic", observer.Query.Data);
            Assert.False(observer.Query.IsException);
        }
        finally
        {
            handler.ReleaseAll("released");
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task CancelAsync_Completes_Only_Once_The_Fetch_Has_Unwound()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>(honourToken: false);

        try
        {
            var observer = client.Subscribe(QueryOptions.Create("unwind", handler.Handle).Build(), () => { });
            Assert.Equal(1, handler.Calls);

            var cancelling = client.CancelAsync("unwind");

            Assert.False(cancelling.IsCompleted);
            Assert.True(observer.Query.IsFetching);

            handler.Release(0, "from-network");
            await cancelling.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(observer.Query.IsIdle);
            Assert.Null(observer.Query.Data);
        }
        finally
        {
            handler.ReleaseAll("released");
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task CancelAsync_Cancels_Every_Query_Under_The_Prefix()
    {
        using var client = NewClient();
        var a = new GatedHandler<string>(honourToken: false);
        var b = new GatedHandler<string>(honourToken: false);

        try
        {
            var first = client.Subscribe(QueryOptions.Create(("users", "a"), a.Handle).Build(), () => { });
            var second = client.Subscribe(QueryOptions.Create(("users", "b"), b.Handle).Build(), () => { });
            Assert.Equal(1, a.Calls);
            Assert.Equal(1, b.Calls);

            var cancelling = client.CancelAsync(ValueTuple.Create("users"));
            Assert.False(cancelling.IsCompleted);

            a.Release(0, "from-network");
            b.Release(0, "from-network");
            await cancelling.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(first.Query.IsIdle);
            Assert.True(second.Query.IsIdle);
            Assert.Null(first.Query.Data);
            Assert.Null(second.Query.Data);
        }
        finally
        {
            a.ReleaseAll("released");
            b.ReleaseAll("released");
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Cancelled_Await_Abandons_The_Wait_And_Keeps_The_Fetch()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        using var cts = new CancellationTokenSource();

        var fetch = client.QueryAsync(QueryOptions.Create("abandoned", handler.Handle).Build(), cts.Token);
        Assert.Equal(1, handler.Calls);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TestContext.Current.CancellationToken));

        var state = client.FindQuery<string>("abandoned")!;
        Assert.True(state.IsFetching);

        var resolved = Signals.UntilAsync(state, s => s.IsResolved, TestContext.Current.CancellationToken);
        handler.Release(0, "from-network");
        await resolved;

        Assert.Equal("from-network", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Cancelled_Fetch_Does_Not_Throw_An_Older_Fetch_Failure()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("stale-error", handler.Handle).ConfigureRetry(retry => retry.Retry(0)).Build();

        var first = client.QueryAsync(options, TestContext.Current.CancellationToken);
        handler.Fail(0, new InvalidOperationException("first"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(client.FindQuery<string>("stale-error")!.IsException);

        var second = client.QueryAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);

        await client.CancelAsync("stale-error").WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Unsubscribing_Does_Not_Cancel_A_Fetch_Someone_Else_Is_Awaiting()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();

        var observer = client.Subscribe(
            QueryOptions.Create("unmounted", handler.Handle).Enabled(false).Build(),
            () => { });
        Assert.Equal(0, handler.Calls);

        var fetch = client.QueryAsync(QueryOptions.Create("unmounted", handler.Handle).Build(), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        observer.Dispose();
        Assert.False(handler.Token(0).IsCancellationRequested);

        handler.Release(0, "from-network");

        Assert.Equal("from-network", await fetch.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Handler_Failing_While_Cancelled_Records_No_Error()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>(honourToken: false);

        try
        {
            var fetch = client.QueryAsync(QueryOptions.Create("aborted", handler.Handle)
                .ConfigureRetry(retry => retry.Retry(0)).Build(), TestContext.Current.CancellationToken);

            var cancelling = client.CancelAsync("aborted");
            Assert.False(cancelling.IsCompleted);

            handler.Fail(0, new IOException("socket aborted"));
            await cancelling.WaitAsync(TestContext.Current.CancellationToken);

            var state = client.FindQuery<string>("aborted")!;
            Assert.False(state.IsException);
            Assert.Null(state.Exception);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            handler.ReleaseAll("released");
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Fetch_Requested_After_A_Cancel_Does_Not_Inherit_It()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>(honourToken: false);
        var options = QueryOptions.Create("superseded", handler.Handle).Build();

        try
        {
            var first = client.QueryAsync(options, TestContext.Current.CancellationToken);
            var cancelling = client.CancelAsync("superseded");
            var second = client.QueryAsync(options, TestContext.Current.CancellationToken);

            Assert.Equal(1, handler.Calls);
            Assert.False(second.IsCompleted);

            handler.Release(0, "stale");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestContext.Current.CancellationToken));
            await cancelling.WaitAsync(TestContext.Current.CancellationToken);

            await handler.Started(1).WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(handler.Token(1).IsCancellationRequested);

            handler.Release(1, "from-network");
            Assert.Equal("from-network", await second.WaitAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            handler.ReleaseAll("released");
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Disposing_The_Client_Cancels_The_Fetches_It_Was_Driving()
    {
        var client = NewClient();
        var handler = new GatedHandler<string>();

        var fetch = client.QueryAsync(QueryOptions.Create("torn-down", handler.Handle).Build(), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        client.Dispose();
        Assert.True(handler.Token(0).IsCancellationRequested);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TestContext.Current.CancellationToken));
    }

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
}
