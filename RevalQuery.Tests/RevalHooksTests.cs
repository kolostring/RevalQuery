using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Tests;

public class RevalHooksTests
{
    private sealed record Req(string Name);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    private static QueryOptions<ValueTuple<string>, string> Opts(string key, Action? onCall = null) =>
        QueryOptions.Create<string>(key, _ =>
        {
            onCall?.Invoke();
            return Task.FromResult(key);
        }).Build();

    private static QueryOptions<ValueTuple<string>, string> Gated(string key, TaskCompletionSource gate) =>
        QueryOptions.Create<string>(key, async _ =>
        {
            await gate.Task;
            return key;
        }).Build();

    private static bool Observed(RevalClient client, string key) =>
        client.FindQuery(key)?.HasObservers == true;

    private static void ReadAtOneSite(RevalHooks hooks, params string[] keys)
    {
        foreach (var key in keys) hooks.Query(Opts(key));
    }

    [Fact]
    public void A_Key_That_Stops_Being_Read_Is_Released_At_The_Next_Sweep()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        ReadAtOneSite(hooks, "a");
        hooks.RenderCompleted();

        ReadAtOneSite(hooks, "b");

        Assert.True(Observed(client, "a"));

        hooks.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
    }

    [Fact]
    public void Keys_Added_And_Removed_In_A_Loop_Follow_What_The_Loop_Read()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        ReadAtOneSite(hooks, "1", "2", "3");
        hooks.RenderCompleted();

        ReadAtOneSite(hooks, "1", "3", "4");
        hooks.RenderCompleted();

        Assert.True(Observed(client, "1"));
        Assert.False(Observed(client, "2"));
        Assert.True(Observed(client, "3"));
        Assert.True(Observed(client, "4"));
    }

    [Fact]
    public void A_Helper_Called_Twice_With_Different_Keys_Keeps_Both()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        for (var render = 0; render < 3; render++)
        {
            ReadAtOneSite(hooks, "first");
            ReadAtOneSite(hooks, "second");
            hooks.RenderCompleted();
        }

        Assert.True(Observed(client, "first"));
        Assert.True(Observed(client, "second"));
    }

    [Fact]
    public void Two_Calls_On_One_Line_Keep_Both()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        for (var render = 0; render < 3; render++)
        {
            hooks.Query(Opts("x")); hooks.Query(Opts("y"));
            hooks.RenderCompleted();
        }

        Assert.True(Observed(client, "x"));
        Assert.True(Observed(client, "y"));
    }

    [Fact]
    public void A_Site_That_Was_Not_Read_Releases_Nothing()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        ReadAtOneSite(hooks, "hidden");
        hooks.RenderCompleted();

        hooks.RenderCompleted();
        hooks.RenderCompleted();

        Assert.True(Observed(client, "hidden"));
    }

    [Fact]
    public void A_Read_Between_Sweeps_Counts_Towards_The_Next_One()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        ReadAtOneSite(hooks, "a");
        hooks.RenderCompleted();

        ReadAtOneSite(hooks, "a", "b");
        hooks.RenderCompleted();

        Assert.True(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
    }

    [Fact]
    public void The_Same_Key_Twice_In_A_Render_Shares_One_Observer()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var handle = hooks.Attach(() => { });
        var calls = 0;

        var first = hooks.Query(Opts("shared", () => calls++));
        var second = hooks.Query(Opts("shared", () => calls++));

        Assert.Same(first, second);
        Assert.Equal(1, calls);

        handle.Dispose();

        Assert.False(Observed(client, "shared"));
    }

    [Fact]
    public void The_Same_Key_At_Two_Sites_Shares_One_Observer_Until_Neither_Holds_It()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var calls = 0;

        var one = hooks.Query(("site", 1), Opts("both", () => calls++));
        var two = hooks.Query(("site", 2), Opts("both", () => calls++));
        hooks.RenderCompleted();

        Assert.Same(one, two);
        Assert.Equal(1, calls);

        hooks.Query(("site", 1), Opts("other"));
        hooks.Query(("site", 2), Opts("both"));
        hooks.RenderCompleted();
        Assert.True(Observed(client, "both"));

        hooks.Query(("site", 1), Opts("other"));
        hooks.Query(("site", 2), Opts("another"));
        hooks.RenderCompleted();
        Assert.False(Observed(client, "both"));
    }

    [Fact]
    public void An_Explicit_Slot_Is_An_Identity_Of_Its_Own()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        foreach (var key in new[] { "a", "b" })
            hooks.Query(("rows", key), Opts(key));
        hooks.RenderCompleted();

        hooks.Query(("rows", "a"), Opts("c"));
        hooks.Query(("rows", "b"), Opts("b"));
        hooks.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
        Assert.True(Observed(client, "c"));
    }

    [Fact]
    public void The_Builder_Overloads_Read_Like_The_Options_Ones()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        var byLine = hooks.Query(QueryOptions.Create<string>("built", _ => Task.FromResult("built")));
        var bySlot = hooks.Query(new object(), QueryOptions.Create<string>("slotted", _ => Task.FromResult("slotted")));

        Assert.NotNull(byLine);
        Assert.NotNull(bySlot);
        Assert.True(Observed(client, "built"));
        Assert.True(Observed(client, "slotted"));
    }

    [Fact]
    public void A_String_Slot_Is_A_Slot_And_Never_A_File_Name()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        hooks.Query("text", Opts("a"));
        hooks.Query("text", QueryOptions.Create<string>("b", _ => Task.FromResult("b")));
        hooks.RenderCompleted();

        hooks.Query("text", Opts("b"));
        hooks.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));

        hooks.Mutation("text", MutationOptions.Create<Req, string>(_ => Task.FromResult("m")));
        hooks.Mutation("text", MutationOptions.Create<Req, string>(_ => Task.FromResult("m")).Build());
    }

    [Fact]
    public void A_Key_Read_With_Another_Result_Type_Throws()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        hooks.Query(Opts("typed"));

        Assert.Throws<InvalidOperationException>(() =>
            hooks.Query(QueryOptions.Create<int>("typed", _ => Task.FromResult(1)).Build()));
    }

    [Fact]
    public async Task A_Repeat_Read_Applies_The_Options_Of_That_Render()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var calls = 0;

        hooks.Query("slot", QueryOptions.Create<string>("toggle", _ =>
        {
            calls++;
            return Task.FromResult("d");
        }).Enabled(false));
        Assert.Equal(0, calls);

        var state = hooks.Query("slot", QueryOptions.Create<string>("toggle", _ =>
        {
            calls++;
            return Task.FromResult("d");
        }).Enabled(true));

        await TestUtils.WaitForStateAsync(state, s => s.IsResolved);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_Mutation_Is_Not_Released_By_A_Sweep()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        var first = hooks.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")).Build());
        hooks.RenderCompleted();
        hooks.RenderCompleted();

        var again = hooks.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("b")).Build());

        Assert.Same(first, again);
    }

    [Fact]
    public async Task A_Repeat_Mutation_Read_Applies_The_New_Options()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        hooks.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-1")).Build());
        var state = hooks.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-2")).Build());

        await state.ExecuteAsync(new Req("a"));

        Assert.Equal("render-2", state.Data);
    }

    [Fact]
    public void Mutations_Are_Told_Apart_By_Key_And_By_Call_Site()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        var a = hooks.Mutation("a", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")).Build());
        var b = hooks.Mutation("b", MutationOptions.Create<Req, string>(_ => Task.FromResult("b")).Build());
        var site = hooks.Mutation(MutationOptions.Create<Req, string>(_ => Task.FromResult("c")));
        var builderKey = hooks.Mutation("a", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")));

        Assert.NotSame(a, b);
        Assert.NotSame(a, site);
        Assert.Same(a, builderKey);
    }

    [Fact]
    public async Task A_Running_Mutation_Survives_Sweeps_And_Reports_To_The_Host()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = 0;
        using var handle = hooks.Attach(() => Interlocked.Increment(ref changed));
        var started = Signal();
        var release = Signal();

        var state = hooks.Mutation("run", MutationOptions.Create<Req, string>(async _ =>
        {
            started.SetResult();
            await release.Task;
            return "done";
        }).Build());

        var run = state.ExecuteAsync(new Req("a"));
        await started.Task;

        hooks.RenderCompleted();
        hooks.RenderCompleted();
        Assert.True(state.IsFetching);
        Assert.True(Volatile.Read(ref changed) > 0);

        var before = Volatile.Read(ref changed);
        release.SetResult();
        await run;

        Assert.True(Volatile.Read(ref changed) > before);
        Assert.Equal("done", hooks.Mutation("run", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build()).Data);
    }

    [Fact]
    public async Task A_Change_The_Render_Did_Not_Read_Reaches_The_Host()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var gate = Signal();
        var changed = Signal();
        using var handle = hooks.Attach(() => changed.TrySetResult());

        var state = hooks.Query(Gated("slow", gate));

        Assert.True(changed.Task.IsCompleted);
        changed = Signal();

        gate.SetResult();
        await changed.Task;

        Assert.True(state.IsResolved);
    }

    [Fact]
    public void A_Change_Raised_While_Reading_Is_Forwarded_Like_Any_Other()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = 0;
        using var handle = hooks.Attach(() => Interlocked.Increment(ref changed));

        var state = hooks.Query(Opts("sync"));

        Assert.True(state.IsResolved);
        Assert.True(changed > 0);

        var settled = changed;
        hooks.Query(Opts("sync"));
        Assert.Equal(settled, changed);
    }

    [Fact]
    public async Task A_Change_Before_Any_Host_Is_Delivered_Once_On_Attach()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var state = hooks.Mutation("early", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build());

        await state.ExecuteAsync(new Req("a"));

        var changed = 0;
        using var handle = hooks.Attach(() => changed++);

        Assert.Equal(1, changed);
    }

    [Fact]
    public void Nothing_Is_Delivered_On_Attach_When_Nothing_Changed()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = 0;

        using var handle = hooks.Attach(() => changed++);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_Second_Attach_Is_Refused_While_One_Is_Attached()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        using var first = hooks.Attach(() => { });

        Assert.Throws<InvalidOperationException>(() => hooks.Attach(() => { }));
    }

    [Fact]
    public async Task Released_Hooks_Do_Not_Tell_The_Renderer()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var gate = Signal();
        var changed = 0;

        var handle = hooks.Attach(() => changed++);
        var state = hooks.Query(Gated("late", gate));
        handle.Dispose();
        changed = 0;

        gate.SetResult();
        await TestUtils.WaitForStateAsync(state, s => s.IsResolved);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Disposing_The_Attach_Handle_Releases_Every_Observer()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var handle = hooks.Attach(() => { });

        hooks.Query(Opts("q1"));
        hooks.Query(Opts("q2"));
        hooks.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build());

        handle.Dispose();

        Assert.False(Observed(client, "q1"));
        Assert.False(Observed(client, "q2"));
    }

    [Fact]
    public void Disposing_The_Attach_Handle_Twice_Is_A_No_Op()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var handle = hooks.Attach(() => { });
        hooks.Query(Opts("q"));

        handle.Dispose();
        handle.Dispose();

        Assert.False(Observed(client, "q"));
    }

    [Fact]
    public void Released_Hooks_Refuse_Reads_And_Attach()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        hooks.Attach(() => { }).Dispose();

        var error = Assert.Throws<InvalidOperationException>(() => hooks.Query(Opts("x")));
        Assert.Contains("released", error.Message);
        Assert.Throws<InvalidOperationException>(() => hooks.Query(new object(), Opts("x")));
        Assert.Throws<InvalidOperationException>(() =>
            hooks.Mutation(MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build()));
        Assert.Throws<InvalidOperationException>(() => hooks.Attach(() => { }));
        Assert.False(Observed(client, "x"));
    }

    [Fact]
    public async Task Released_Hooks_Ignore_Sweeps_And_Notifications()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var gate = Signal();
        var changed = 0;
        var handle = hooks.Attach(() => changed++);

        var state = hooks.Query(Gated("gone", gate));
        handle.Dispose();
        changed = 0;

        hooks.RenderCompleted();

        gate.SetResult();
        await TestUtils.WaitForStateAsync(state, s => s.IsResolved);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Hooks_Are_Not_Disposable()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(RevalHooks)));
    }

    [Fact]
    public void AddRevalQuery_Gives_Every_Resolution_Its_Own_Hooks()
    {
        var services = new ServiceCollection();
        services.AddRevalQuery();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<RevalHooks>();
        var second = scope.ServiceProvider.GetRequiredService<RevalHooks>();

        Assert.NotSame(first, second);

        first.Query(Opts("a"));
        first.Attach(() => { }).Dispose();

        Assert.Throws<InvalidOperationException>(() => first.Query(Opts("b")));
        Assert.NotNull(second.Query(Opts("b")));
    }

    [Fact]
    public void Creating_Hooks_On_A_Disposed_Client_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.CreateHooks());
    }
}
