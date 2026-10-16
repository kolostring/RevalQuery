using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Core.Tests;

public class RevalHooksTests
{
    private readonly FakeTimeProvider _clock = new();

    private sealed record Req(string Name);

    private RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock });

    private static QueryOptions<ValueTuple<string>, string> Opts(string key, Counter? calls = null) =>
        QueryOptions.Create<string>(key, _ =>
        {
            calls?.Increment();
            return Task.FromResult(key);
        }).Build();

    private static QueryOptions<ValueTuple<string>, string> Gated(string key, GatedHandler<string> handler) =>
        QueryOptions.Create<string>(key, handler.Handle).Build();

    private static bool Observed(RevalClient client, string key) =>
        client.FindQuery(key)?.HasObservers == true;

    private static void ReadAtOneSite(RevalHooks hooks, params string[] keys)
    {
        foreach (var key in keys) hooks.Query(Opts(key));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Same_Key_Twice_In_A_Render_Shares_One_Observer()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var handle = hooks.Attach(() => { });
        var calls = new Counter();

        var first = hooks.Query(Opts("shared", calls));
        var second = hooks.Query(Opts("shared", calls));

        Assert.Same(first, second);
        Assert.Equal(1, calls.Value);

        handle.Dispose();

        Assert.False(Observed(client, "shared"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Same_Key_At_Two_Sites_Shares_One_Observer_Until_Neither_Holds_It()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var calls = new Counter();

        var one = hooks.Query(("site", 1), Opts("both", calls));
        var two = hooks.Query(("site", 2), Opts("both", calls));
        hooks.RenderCompleted();

        Assert.Same(one, two);
        Assert.Equal(1, calls.Value);

        hooks.Query(("site", 1), Opts("other"));
        hooks.Query(("site", 2), Opts("both"));
        hooks.RenderCompleted();
        Assert.True(Observed(client, "both"));

        hooks.Query(("site", 1), Opts("other"));
        hooks.Query(("site", 2), Opts("another"));
        hooks.RenderCompleted();
        Assert.False(Observed(client, "both"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Key_Read_With_Another_Result_Type_Throws()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        hooks.Query(Opts("typed"));

        Assert.Throws<InvalidOperationException>(() =>
            hooks.Query(QueryOptions.Create<int>("typed", _ => Task.FromResult(1)).Build()));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Repeat_Read_Applies_The_Options_Of_That_Render()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var calls = new Counter();

        hooks.Query("slot", QueryOptions.Create<string>("toggle", _ =>
        {
            calls.Increment();
            return Task.FromResult("d");
        }).Enabled(false));
        Assert.Equal(0, calls.Value);

        var state = hooks.Query("slot", QueryOptions.Create<string>("toggle", _ =>
        {
            calls.Increment();
            return Task.FromResult("d");
        }).Enabled(true));

        Assert.True(state.IsResolved);
        Assert.Equal(1, calls.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Repeat_Mutation_Read_Applies_The_New_Options()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        hooks.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-1")).Build());
        var state = hooks.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-2")).Build());

        await state.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        Assert.Equal("render-2", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Running_Mutation_Survives_Sweeps_And_Reports_To_The_Host()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = new Counter();
        using var handle = hooks.Attach(() => changed.Increment());
        var h = new GatedHandler<string>();

        var state = hooks.Mutation("run", MutationOptions.Create<Req, string>(h.Handle).Build());

        var run = state.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);
        await h.Started(0).WaitAsync(TestContext.Current.CancellationToken);

        hooks.RenderCompleted();
        hooks.RenderCompleted();
        Assert.True(state.IsFetching);
        Assert.Equal(1, changed.Value);

        h.Release(0, "done");
        await run.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, changed.Value);
        Assert.Equal("done", hooks.Mutation("run", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build()).Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Change_The_Render_Did_Not_Read_Reaches_The_Host()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var h = new GatedHandler<string>();
        var first = new Gate();
        var second = new Gate();
        using var handle = hooks.Attach(() => (first.Task.IsCompleted ? second : first).Open());

        var state = hooks.Query(Gated("slow", h));

        Assert.True(first.Task.IsCompleted);

        h.Release(0, "slow");
        await second.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(state.IsResolved);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Change_Raised_While_Reading_Is_Forwarded_Like_Any_Other()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = new Counter();
        using var handle = hooks.Attach(() => changed.Increment());

        var state = hooks.Query(Opts("sync"));

        Assert.True(state.IsResolved);
        Assert.Equal(2, changed.Value);

        hooks.Query(Opts("sync"));
        Assert.Equal(2, changed.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Change_Before_Any_Host_Is_Delivered_Once_On_Attach()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var state = hooks.Mutation("early", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build());

        await state.ExecuteAsync(new Req("a"), TestContext.Current.CancellationToken);

        var changed = new Counter();
        using var handle = hooks.Attach(() => changed.Increment());

        Assert.Equal(1, changed.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Nothing_Is_Delivered_On_Attach_When_Nothing_Changed()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var changed = new Counter();

        using var handle = hooks.Attach(() => changed.Increment());

        Assert.Equal(0, changed.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Second_Attach_Is_Refused_While_One_Is_Attached()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();

        using var first = hooks.Attach(() => { });

        Assert.Throws<InvalidOperationException>(() => hooks.Attach(() => { }));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Released_Hooks_Do_Not_Tell_The_Renderer()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var h = new GatedHandler<string>();
        var changed = new Counter();

        var handle = hooks.Attach(() => changed.Increment());
        var state = hooks.Query(Gated("late", h));
        handle.Dispose();
        var baseline = changed.Value;

        // Created before the release: it can only complete inside the final notification, and
        // every handler subscribed earlier has run by then.
        var settled = Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle, TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);
        h.Release(0, "late");
        await settled;

        Assert.Equal(baseline, changed.Value);
        Assert.Equal("late", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Released_Hooks_Ignore_Sweeps_And_Notifications()
    {
        using var client = NewClient();
        var hooks = client.CreateHooks();
        var h = new GatedHandler<string>();
        var changed = new Counter();
        var handle = hooks.Attach(() => changed.Increment());

        var state = hooks.Query(Gated("gone", h));
        handle.Dispose();
        var baseline = changed.Value;

        hooks.RenderCompleted();

        var settled = Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle, TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);
        h.Release(0, "gone");
        await settled;

        Assert.Equal(baseline, changed.Value);
        Assert.Equal("gone", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Hooks_Are_Not_Disposable()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(RevalHooks)));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
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

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Creating_Hooks_On_A_Disposed_Client_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.CreateHooks());
    }
}
