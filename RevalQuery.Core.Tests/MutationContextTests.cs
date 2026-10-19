using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class MutationContextTests
{
    private sealed record Req(string Name);

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    /// <summary>A mutation defined once, with no client to inject: it reaches the client through the context.</summary>
    private static class Factory
    {
        public static MutationOptionsBuilder<Req, string> Save(Func<MutationHandlerExecutionContext<Req>, Task<string>> handler) =>
            MutationOptions.Create<Req, string>(handler)
                .OnSettled(ctx =>
                {
                    ctx.Client.Invalidate("todos");
                    return Task.CompletedTask;
                });
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Static_Factorys_OnSettled_Invalidates_A_Query_Through_The_Context()
    {
        using var client = NewClient();
        var calls = 0;
        var todos = QueryOptions.Create<string>("todos", _ => Task.FromResult($"load-{Interlocked.Increment(ref calls)}"))
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        await client.QueryAsync(todos, TestContext.Current.CancellationToken);
        await client.QueryAsync(todos, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);

        var observer = client.CreateMutation(Factory.Save(_ => Task.FromResult("ok")).Build(), () => { });
        await observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal("load-2", await client.QueryAsync(todos, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Handler_Context_Carries_The_Client()
    {
        using var client = NewClient();
        RevalClient? seen = null;

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(ctx => { seen = ctx.Client; return Task.FromResult("ok"); }).Build(),
            () => { });
        await observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Same(client, seen);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Callback_Contexts_Carry_The_Params_The_Outcome_And_The_Client()
    {
        using var client = NewClient();
        var log = new ConcurrentQueue<string>();
        var clients = new ConcurrentQueue<RevalClient>();

        MutationOptionsBuilder<Req, string> Build(Func<MutationHandlerExecutionContext<Req>, Task<string>> handler) =>
            MutationOptions.Create<Req, string>(handler)
                .OnMutate(ctx => { clients.Enqueue(ctx.Client); log.Enqueue($"mutate:{ctx.Params.Name}"); return Task.CompletedTask; })
                .OnResolved(ctx => { clients.Enqueue(ctx.Client); log.Enqueue($"resolved:{ctx.Params.Name}:{ctx.Result}"); return Task.CompletedTask; })
                .OnException(ctx => { clients.Enqueue(ctx.Client); log.Enqueue($"exception:{ctx.Params.Name}:{ctx.Exception.Message}"); return Task.CompletedTask; })
                .OnSettled(ctx => { clients.Enqueue(ctx.Client); log.Enqueue($"settled:{ctx.Params.Name}:{ctx.Result}:{ctx.Exception?.Message}"); return Task.CompletedTask; });

        var ok = client.CreateMutation(Build(_ => Task.FromResult("v")).Build(), () => { });
        await ok.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);
        var bad = client.CreateMutation(Build(_ => Task.FromException<string>(new InvalidOperationException("boom"))).Build(), () => { });
        await bad.State.ExecuteAsync(new Req("b"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["mutate:a", "resolved:a:v", "settled:a:v:", "mutate:b", "exception:b:boom", "settled:b::boom"],
            log.ToArray());
        Assert.All(clients, c => Assert.Same(client, c));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Callback_Chained_Onto_The_Factorys_Builder_Replaces_The_Base_One()
    {
        using var client = NewClient();
        var log = new ConcurrentQueue<string>();

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(_ => Task.FromResult("ok"))
                .OnResolved(_ => { log.Enqueue("base"); return Task.CompletedTask; })
                .OnResolved(_ => { log.Enqueue("chained"); return Task.CompletedTask; })
                .Build(),
            () => { });
        await observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal(["chained"], log.ToArray());
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Per_Call_Callbacks_Run_After_The_Options_Callbacks_And_Only_For_The_Latest_Run()
    {
        using var client = NewClient();
        var h = new GatedHandler<string>();
        var log = new ConcurrentQueue<string>();

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(h.Handle)
                .OnResolved(ctx => { log.Enqueue($"options:resolved:{ctx.Params.Name}"); return Task.CompletedTask; })
                .OnSettled(ctx => { log.Enqueue($"options:settled:{ctx.Params.Name}"); return Task.CompletedTask; })
                .Build(),
            () => { });

        MutateOptions<Req, string> PerCall() => new(
            OnResolved: ctx => { log.Enqueue($"call:resolved:{ctx.Params.Name}"); return Task.CompletedTask; },
            OnSettled: ctx => { log.Enqueue($"call:settled:{ctx.Params.Name}"); return Task.CompletedTask; });

        var first = observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken, PerCall());
        await h.Started(0).WaitAsync(TestContext.Current.CancellationToken);
        var second = observer.State.ExecuteAsync(new Req("b"), TestContext.Current.CancellationToken, PerCall());
        await h.Started(1).WaitAsync(TestContext.Current.CancellationToken);

        h.Release(0, "a");
        await first.WaitAsync(TestContext.Current.CancellationToken);
        h.Release(1, "b");
        await second.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["options:resolved:a", "options:settled:a",
             "options:resolved:b", "call:resolved:b", "options:settled:b", "call:settled:b"],
            log.ToArray());
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Per_Call_Callback_Contexts_Carry_The_Client()
    {
        using var client = NewClient();
        var seen = new ConcurrentQueue<RevalClient>();

        var observer = client.CreateMutation(MutationOptions.Create<Req, string>(_ => Task.FromResult("ok")).Build(), () => { });
        await observer.State.ExecuteAsync(
            new Req("a"),
            TestContext.Current.CancellationToken,
            new MutateOptions<Req, string>(
                OnResolved: ctx => { seen.Enqueue(ctx.Client); return Task.CompletedTask; },
                OnSettled: ctx => { seen.Enqueue(ctx.Client); return Task.CompletedTask; }));

        Assert.Equal(2, seen.Count);
        Assert.All(seen, c => Assert.Same(client, c));
    }
}
