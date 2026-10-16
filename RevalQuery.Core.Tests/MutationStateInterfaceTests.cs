using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Mutation;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation.Options;

namespace RevalQuery.Core.Tests;

public class MutationStateInterfaceTests
{
    private sealed record Req(string Name);

    private static string Describe<TResponse>(IMutationState<TResponse> mutation) =>
        mutation switch
        {
            { IsIdle: true } => "idle",
            { IsFetching: true } => "fetching",
            { IsResolved: true } => $"resolved:{mutation.Data}",
            { IsException: true } => $"exception:{mutation.Exception!.Message}",
            _ => "unknown"
        };

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Mutation_Can_Be_Watched_And_Reset_Through_The_Interface_Alone()
    {
        var gate = new Gate();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), new ManualEviction());
        using var observer = client.CreateMutation(MutationOptions.Create<Req, string>(async ctx =>
        {
            await gate.Task;
            return ctx.Params.Name == "fail" ? throw new InvalidOperationException("boom") : ctx.Params.Name;
        }).Build(), () => { });
        IMutationState<string> mutation = observer.State;
        var seen = new List<string>();
        mutation.OnChanged += () => seen.Add(Describe(mutation));

        Assert.Equal("idle", Describe(mutation));

        var run = observer.State.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);
        Assert.Equal("fetching", Describe(mutation));
        gate.Open();
        await run.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("resolved:a", Describe(mutation));

        await observer.State.ExecuteAsync(new Req("fail"), TestContext.Current.CancellationToken).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("exception:boom", Describe(mutation));
        Assert.Null(mutation.Data);

        mutation.Reset();
        Assert.Equal("idle", Describe(mutation));
        Assert.Null(mutation.Exception);

        Assert.Equal(["fetching", "resolved:a", "fetching", "exception:boom", "idle"], seen);
    }
}
