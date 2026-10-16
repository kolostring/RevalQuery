using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.TestKit.Tests;

public class GatedHandlerTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static QueryHandlerExecutionContext<ValueTuple<string>> Context(CancellationToken ct = default) =>
        new() { Key = ValueTuple.Create("a"), ServiceProvider = Services, CancellationToken = ct };

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Each_Call_Records_Its_Start()
    {
        var handler = new GatedHandler<string>();

        _ = handler.Handle(Context(TestContext.Current.CancellationToken));
        _ = handler.Handle(Context(TestContext.Current.CancellationToken));

        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        await handler.Started(1).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
        Assert.False(handler.Started(2).IsCompleted);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Call_Returns_Only_The_Value_It_Is_Released_With()
    {
        var handler = new GatedHandler<string>();
        var first = handler.Handle(Context(TestContext.Current.CancellationToken));
        var second = handler.Handle(Context(TestContext.Current.CancellationToken));

        handler.Release(1, "second");
        Assert.Equal("second", await second.WaitAsync(TestContext.Current.CancellationToken));
        Assert.False(first.IsCompleted);

        handler.Release(0, "first");
        Assert.Equal("first", await first.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Releasing_All_Releases_Every_Call_Made_And_Every_Call_To_Come()
    {
        var handler = new GatedHandler<string>();
        var before = handler.Handle(Context(TestContext.Current.CancellationToken));

        handler.ReleaseAll("v");

        Assert.Equal("v", await before.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("v", await handler.Handle(Context(TestContext.Current.CancellationToken)).WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Call_Released_In_Advance_Completes_Synchronously()
    {
        var handler = new GatedHandler<string>();
        handler.Release(0, "v");

        var call = handler.Handle(Context(TestContext.Current.CancellationToken));

        Assert.True(call.IsCompletedSuccessfully);
        Assert.Equal("v", await call);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Query_Whose_Handler_Was_Released_In_Advance_Resolves_Synchronously()
    {
        var handler = new GatedHandler<string>();
        handler.ReleaseAll("v");
        using var client = new RevalClient(Services, new RevalQueryOptions(), new ManualEviction());

        var query = client.QueryAsync(QueryOptions.Create("a", handler.Handle).Build(), TestContext.Current.CancellationToken);

        Assert.True(query.IsCompletedSuccessfully);
        Assert.Equal("v", await query);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Failed_Call_Throws_The_Exception()
    {
        var handler = new GatedHandler<string>();
        var boom = new InvalidOperationException("boom");
        var call = handler.Handle(Context(TestContext.Current.CancellationToken));

        handler.Fail(0, boom);

        Assert.Same(boom, await Assert.ThrowsAsync<InvalidOperationException>(() => call.WaitAsync(TestContext.Current.CancellationToken)));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Releasing_A_Call_Twice_Is_A_Test_Error()
    {
        var handler = new GatedHandler<string>();
        handler.Release(0, "v");

        Assert.Throws<InvalidOperationException>(() => handler.Release(0, "again"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Each_Call_Exposes_The_Token_It_Received()
    {
        var handler = new GatedHandler<string>();
        using var cts = new CancellationTokenSource();

        _ = handler.Handle(Context(cts.Token));
        var second = TestContext.Current.CancellationToken;
        _ = handler.Handle(Context(second));

        await handler.Started(1).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cts.Token, handler.Token(0));
        Assert.Equal(second, handler.Token(1));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Cancelled_Signal_Completes_When_That_Calls_Token_Fires()
    {
        var handler = new GatedHandler<string>();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        _ = handler.Handle(Context(first.Token));
        _ = handler.Handle(Context(second.Token));

        second.Cancel();

        await handler.Cancelled(1).WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(handler.Cancelled(0).IsCompleted);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Call_That_Honours_Its_Token_Ends_When_The_Token_Fires()
    {
        var handler = new GatedHandler<string>();
        using var cts = new CancellationTokenSource();
        var call = handler.Handle(Context(cts.Token));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Call_That_Ignores_Its_Token_Waits_For_Its_Release()
    {
        var handler = new GatedHandler<string>(honourToken: false);
        using var cts = new CancellationTokenSource();
        var call = handler.Handle(Context(cts.Token));

        cts.Cancel();
        await handler.Cancelled(0).WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);

        handler.Release(0, "late");
        Assert.Equal("late", await call.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Mutation_Call_Is_Gated_Like_A_Query_Call()
    {
        var handler = new GatedHandler<string>();
        using var client = new RevalClient(Services, new RevalQueryOptions(), new ManualEviction());
        var observer = client.CreateMutation(MutationOptions.Create<Req, string>(handler.Handle).Build(), () => { });

        var run = observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        handler.Release(0, "done");
        await run.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("done", observer.State.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Mutation_Call_Exposes_The_Token_Its_Context_Carries()
    {
        var handler = new GatedHandler<string>();
        using var cts = new CancellationTokenSource();

        _ = handler.Handle(new MutationHandlerExecutionContext<Req>
            { Params = new Req("a"), ServiceProvider = Services, CancellationToken = cts.Token });

        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cts.Token, handler.Token(0));
    }

    private sealed record Req(string Name);
}
