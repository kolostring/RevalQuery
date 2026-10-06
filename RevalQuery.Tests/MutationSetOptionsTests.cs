using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Tracking;

namespace RevalQuery.Tests;

public class MutationSetOptionsTests
{
    private sealed record Req(string Name);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueryClient NewClient(IServiceProvider? sp = null) =>
        new(sp ?? new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    private static MutationOptions<Req, string> Logging(
        string label,
        ConcurrentQueue<string> log,
        Func<MutationHandlerExecutionContext<Req>, Task<string>> handler)
    {
        return MutationOptions.Create<Req, string>(handler)
            .OnMutate(p => { log.Enqueue($"{label}:mutate:{p.Name}"); return Task.CompletedTask; })
            .OnResolved((_, p) => { log.Enqueue($"{label}:resolved:{p.Name}"); return Task.CompletedTask; })
            .OnException((_, p) => { log.Enqueue($"{label}:exception:{p.Name}"); return Task.CompletedTask; })
            .OnSettled((_, _, p) => { log.Enqueue($"{label}:settled:{p.Name}"); return Task.CompletedTask; })
            .Build();
    }

    [Fact]
    public async Task Options_Set_During_The_Handler_Fire_Their_Callbacks_And_The_Old_Ones_Do_Not()
    {
        var log = new ConcurrentQueue<string>();
        var started = Signal();
        var release = Signal();
        using var client = NewClient();

        var observer = client.CreateMutation(Logging("old", log, async ctx =>
        {
            started.SetResult();
            await release.Task;
            return ctx.Params.Name;
        }), () => { });

        var run = observer.State.ExecuteAsync(new Req("a"));
        await started.Task;

        observer.SetOptions(Logging("new", log, _ => Task.FromResult("unused")));
        release.SetResult();
        await run;

        Assert.Equal(
            ["old:mutate:a", "new:resolved:a", "new:settled:a"],
            log.ToArray());
        Assert.Equal("a", observer.State.Data);
    }

    [Fact]
    public async Task Options_Set_During_The_Handler_Also_Reroute_A_Failure()
    {
        var log = new ConcurrentQueue<string>();
        var started = Signal();
        var release = Signal();
        using var client = NewClient();

        var observer = client.CreateMutation(Logging("old", log, async _ =>
        {
            started.SetResult();
            await release.Task;
            throw new InvalidOperationException("boom");
        }), () => { });

        var run = observer.State.ExecuteAsync(new Req("a"));
        await started.Task;

        observer.SetOptions(Logging("new", log, _ => Task.FromResult("unused")));
        release.SetResult();
        await run;

        Assert.Equal(["old:mutate:a", "new:exception:a", "new:settled:a"], log.ToArray());
        Assert.True(observer.State.IsException);
    }

    [Fact]
    public async Task Options_Set_Between_Retry_Attempts_Supply_The_Next_Handler_But_Not_The_Retry_Count()
    {
        var started = Signal();
        var release = Signal();
        var newCalls = 0;
        using var client = NewClient();

        var original = MutationOptions.Create<Req, string>(async _ =>
            {
                started.SetResult();
                await release.Task;
                throw new InvalidOperationException("first attempt");
            })
            .ConfigureRetry(r => r.Retry(2, _ => TimeSpan.Zero))
            .Build();

        var observer = client.CreateMutation(original, () => { });
        var run = observer.State.ExecuteAsync(new Req("a"));
        await started.Task;

        observer.SetOptions(MutationOptions.Create<Req, string>(_ =>
            {
                Interlocked.Increment(ref newCalls);
                throw new InvalidOperationException("later attempt");
            })
            .ConfigureRetry(r => r.Retry(0))
            .Build());

        release.SetResult();
        await run;

        Assert.Equal(2, newCalls);
        Assert.True(observer.State.IsException);
        Assert.Equal("later attempt", observer.State.Exception!.Message);
    }

    [Fact]
    public async Task A_Handler_Set_Between_Attempts_Can_Be_The_One_That_Succeeds()
    {
        var started = Signal();
        var release = Signal();
        using var client = NewClient();

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(async _ =>
                {
                    started.SetResult();
                    await release.Task;
                    throw new InvalidOperationException("first attempt");
                })
                .ConfigureRetry(r => r.Retry(1, _ => TimeSpan.Zero))
                .Build(),
            () => { });

        var run = observer.State.ExecuteAsync(new Req("a"));
        await started.Task;

        observer.SetOptions(MutationOptions.Create<Req, string>(ctx => Task.FromResult($"fixed-{ctx.Params.Name}")).Build());
        release.SetResult();
        await run;

        Assert.True(observer.State.IsResolved);
        Assert.Equal("fixed-a", observer.State.Data);
    }

    [Fact]
    public async Task Of_Two_Concurrent_Runs_Only_The_Latest_Reads_The_New_Options()
    {
        var log = new ConcurrentQueue<string>();
        var startedA = Signal();
        var startedB = Signal();
        var gateA = Signal();
        var gateB = Signal();
        using var client = NewClient();

        async Task<string> Handler(MutationHandlerExecutionContext<Req> ctx)
        {
            var (started, gate) = ctx.Params.Name == "a" ? (startedA, gateA) : (startedB, gateB);
            started.SetResult();
            await gate.Task;
            return ctx.Params.Name;
        }

        var observer = client.CreateMutation(Logging("old", log, Handler), () => { });

        var runA = observer.State.ExecuteAsync(new Req("a"));
        await startedA.Task;
        var runB = observer.State.ExecuteAsync(new Req("b"));
        await startedB.Task;

        observer.SetOptions(Logging("new", log, Handler));

        gateA.SetResult();
        gateB.SetResult();
        await Task.WhenAll(runA, runB);

        var fired = log.ToArray();

        Assert.Contains("old:resolved:a", fired);
        Assert.Contains("old:settled:a", fired);
        Assert.Contains("new:resolved:b", fired);
        Assert.Contains("new:settled:b", fired);
        Assert.DoesNotContain("new:resolved:a", fired);
        Assert.DoesNotContain("old:resolved:b", fired);
    }

    [Fact]
    public async Task Per_Call_Callbacks_Are_Not_Touched_By_New_Options()
    {
        var log = new ConcurrentQueue<string>();
        var started = Signal();
        var release = Signal();
        using var client = NewClient();

        var observer = client.CreateMutation(Logging("old", log, async ctx =>
        {
            started.SetResult();
            await release.Task;
            return ctx.Params.Name;
        }), () => { });

        var run = observer.State.ExecuteAsync(
            new Req("a"),
            mutateOptions: new MutateOptions<Req, string>(
                OnResolved: (_, p) => { log.Enqueue($"call:resolved:{p.Name}"); return Task.CompletedTask; },
                OnSettled: (_, _, p) => { log.Enqueue($"call:settled:{p.Name}"); return Task.CompletedTask; }));
        await started.Task;

        observer.SetOptions(Logging("new", log, _ => Task.FromResult("unused")));
        release.SetResult();
        await run;

        Assert.Contains("call:resolved:a", log);
        Assert.Contains("call:settled:a", log);
    }

    [Fact]
    public async Task Options_Set_While_Idle_Are_Used_By_The_Next_Run()
    {
        var log = new ConcurrentQueue<string>();
        using var client = NewClient();

        var observer = client.CreateMutation(
            Logging("old", log, ctx => Task.FromResult($"old-{ctx.Params.Name}")), () => { });

        await observer.State.ExecuteAsync(new Req("a"));
        log.Clear();

        observer.SetOptions(Logging("new", log, ctx => Task.FromResult($"new-{ctx.Params.Name}")));
        await observer.State.ExecuteAsync(new Req("b"));

        Assert.Equal(["new:mutate:b", "new:resolved:b", "new:settled:b"], log.ToArray());
        Assert.Equal("new-b", observer.State.Data);
    }

    [Fact]
    public async Task Options_Set_After_A_Run_Settled_Do_Not_Reach_It_And_Reach_The_Next()
    {
        var log = new ConcurrentQueue<string>();
        var started = Signal();
        var release = Signal();
        using var client = NewClient();

        var observer = client.CreateMutation(Logging("v1", log, async ctx =>
        {
            started.SetResult();
            await release.Task;
            return ctx.Params.Name;
        }), () => { });

        var first = observer.State.ExecuteAsync(new Req("a"));
        await started.Task;
        release.SetResult();
        await first;

        observer.SetOptions(Logging("v2", log, ctx => Task.FromResult(ctx.Params.Name)));
        await observer.State.ExecuteAsync(new Req("b"));

        Assert.Equal(
            ["v1:mutate:a", "v1:resolved:a", "v1:settled:a", "v2:mutate:b", "v2:resolved:b", "v2:settled:b"],
            log.ToArray());
    }

    [Fact]
    public async Task CreateMutation_Gives_Handlers_The_Clients_Service_Provider()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? seen = null;
        using var client = NewClient(sp);

        var observer = client.CreateMutation(
            MutationOptions.Create<Req, string>(ctx =>
            {
                seen = ctx.ServiceProvider;
                return Task.FromResult("done");
            }).Build(),
            () => { });

        await observer.State.ExecuteAsync(new Req("a"));

        Assert.Same(sp, seen);
    }

    [Fact]
    public void CreateMutation_After_The_Client_Is_Disposed_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.CreateMutation(
            MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build(), () => { }));
    }

    [Fact]
    public async Task Observe_Creates_On_The_First_Call_Then_Reapplies_Options()
    {
        var changes = 0;
        using var client = NewClient();
        MutationObserver<Req, string>? slot = null;

        var first = client.Observe(ref slot,
            MutationOptions.Create<Req, string>(_ => Task.FromResult("one")).Build(),
            () => Interlocked.Increment(ref changes));
        var created = slot;

        var second = client.Observe(ref slot,
            MutationOptions.Create<Req, string>(_ => Task.FromResult("two")).Build(),
            () => { });

        Assert.Same(created, slot);
        Assert.Same(first, second);

        await second.ExecuteAsync(new Req("a"));

        Assert.Equal("two", second.Data);

        Assert.True(Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public async Task A_Tracker_Applies_The_Options_Of_Every_Render()
    {
        using var client = NewClient();
        var tracker = client.CreateTracker();

        MutationState<Req, string> Run(MutationOptions<Req, string> options) => tracker.Mutation(options);

        var first = Run(MutationOptions.Create<Req, string>(_ => Task.FromResult("render-1")).Build());
        var second = Run(MutationOptions.Create<Req, string>(_ => Task.FromResult("render-2")).Build());

        Assert.Same(first, second);

        await second.ExecuteAsync(new Req("a"));

        Assert.Equal("render-2", second.Data);
    }
}
