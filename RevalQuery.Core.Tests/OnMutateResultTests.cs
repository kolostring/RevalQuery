using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.TestKit;

namespace RevalQuery.Core.Tests;

public class OnMutateResultTests
{
    private sealed record Req(string Name);

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task OnMutates_Result_Reaches_OnResolved_And_OnSettled_Typed()
    {
        using var client = NewClient();
        var seen = new ConcurrentQueue<string>();

        var options = MutationOptions.Create<Req, string>(_ => Task.FromResult("ok"))
            .OnMutate(ctx => Task.FromResult(ctx.Params.Name + "-rollback"))
            .OnResolved(ctx => { seen.Enqueue("resolved:" + ctx.OnMutateResult.ToUpperInvariant()); return Task.CompletedTask; })
            .OnSettled(ctx => { seen.Enqueue("settled:" + ctx.OnMutateResult.Length); return Task.CompletedTask; })
            .Build();

        MutationState<Req, string> state = client.CreateMutation(options, () => { }).State;
        await state.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal(["resolved:A-ROLLBACK", "settled:10"], seen.ToArray());
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task OnMutates_Result_Reaches_OnException_Typed()
    {
        using var client = NewClient();
        int? seen = null;

        var options = MutationOptions.Create<Req, string>(_ => throw new InvalidOperationException())
            .OnMutate(_ => Task.FromResult(42))
            .OnException(ctx => { seen = ctx.OnMutateResult; return Task.CompletedTask; })
            .Build();

        await client.CreateMutation(options, () => { }).State
            .ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal(42, seen);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Overlapping_Runs_Each_Keep_Their_Own_Result()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var rolledBack = new ConcurrentQueue<string>();

        var options = MutationOptions.Create<Req, string>(handler.Handle)
            .OnMutate(ctx => Task.FromResult("undo-" + ctx.Params.Name))
            .OnException(ctx => { rolledBack.Enqueue(ctx.OnMutateResult); return Task.CompletedTask; })
            .Build();
        var state = client.CreateMutation(options, () => { }).State;
        var ct = TestContext.Current.CancellationToken;

        var first = state.ExecuteAsync(new Req("first"), ct);
        await handler.Started(0);
        var second = state.ExecuteAsync(new Req("second"), ct);
        await handler.Started(1);

        handler.Fail(0, new InvalidOperationException());
        await first;

        Assert.Equal(["undo-first"], rolledBack.ToArray());

        handler.Release(1, "ok");
        await second;
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Per_Call_Callbacks_See_The_Same_Runs_Result()
    {
        using var client = NewClient();
        object? seenResolved = null, seenSettled = null;

        var options = MutationOptions.Create<Req, string>(_ => Task.FromResult("ok"))
            .OnMutate(ctx => Task.FromResult("undo-" + ctx.Params.Name))
            .Build();
        var state = client.CreateMutation(options, () => { }).State;

        await state.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken, new MutateOptions<Req, string>(
            OnResolved: ctx => { seenResolved = ctx.OnMutateResult; return Task.CompletedTask; },
            OnSettled: ctx => { seenSettled = ctx.OnMutateResult; return Task.CompletedTask; }));

        Assert.Equal("undo-a", seenResolved);
        Assert.Equal("undo-a", seenSettled);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task When_OnMutate_Throws_The_Typed_Exception_And_Settled_Callbacks_Run_With_Default()
    {
        using var client = NewClient();
        var seen = new ConcurrentQueue<string>();

        var options = MutationOptions.Create<Req, string>(_ => Task.FromResult("never"))
            .OnMutate<int>(_ => throw new InvalidOperationException())
            .OnException(ctx => { seen.Enqueue("exception:" + ctx.OnMutateResult); return Task.CompletedTask; })
            .OnSettled(ctx => { seen.Enqueue("settled:" + ctx.OnMutateResult); return Task.CompletedTask; })
            .Build();

        await client.CreateMutation(options, () => { }).State
            .ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal(["exception:0", "settled:0"], seen.ToArray());
    }
}
