using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Scope;

namespace RevalQuery.Tests;

public class QueryScopeTests
{
    private sealed record Req(string Name);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueryClient NewClient() =>
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

    private static bool Observed(QueryClient client, string key) =>
        client.FindQuery(key)?.HasObservers == true;

    private static void ReadAtOneSite(QueryScope scope, params string[] keys)
    {
        foreach (var key in keys) scope.Query(Opts(key));
    }

    [Fact]
    public void A_Key_That_Stops_Being_Read_Is_Released_At_The_Next_Sweep()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        ReadAtOneSite(scope, "a");
        scope.RenderCompleted();

        ReadAtOneSite(scope, "b");

        Assert.True(Observed(client, "a"));

        scope.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
    }

    [Fact]
    public void Keys_Added_And_Removed_In_A_Loop_Follow_What_The_Loop_Read()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        ReadAtOneSite(scope, "1", "2", "3");
        scope.RenderCompleted();

        ReadAtOneSite(scope, "1", "3", "4");
        scope.RenderCompleted();

        Assert.True(Observed(client, "1"));
        Assert.False(Observed(client, "2"));
        Assert.True(Observed(client, "3"));
        Assert.True(Observed(client, "4"));
    }

    [Fact]
    public void A_Helper_Called_Twice_With_Different_Keys_Keeps_Both()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        for (var render = 0; render < 3; render++)
        {
            ReadAtOneSite(scope, "first");
            ReadAtOneSite(scope, "second");
            scope.RenderCompleted();
        }

        Assert.True(Observed(client, "first"));
        Assert.True(Observed(client, "second"));
    }

    [Fact]
    public void Two_Calls_On_One_Line_Keep_Both()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        for (var render = 0; render < 3; render++)
        {
            scope.Query(Opts("x")); scope.Query(Opts("y"));
            scope.RenderCompleted();
        }

        Assert.True(Observed(client, "x"));
        Assert.True(Observed(client, "y"));
    }

    [Fact]
    public void A_Site_That_Was_Not_Read_Releases_Nothing()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        ReadAtOneSite(scope, "hidden");
        scope.RenderCompleted();

        scope.RenderCompleted();
        scope.RenderCompleted();

        Assert.True(Observed(client, "hidden"));
    }

    [Fact]
    public void A_Read_Between_Sweeps_Counts_Towards_The_Next_One()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        ReadAtOneSite(scope, "a");
        scope.RenderCompleted();

        ReadAtOneSite(scope, "a", "b");
        scope.RenderCompleted();

        Assert.True(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
    }

    [Fact]
    public void The_Same_Key_Twice_In_A_Render_Shares_One_Observer()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var calls = 0;

        var first = scope.Query(Opts("shared", () => calls++));
        var second = scope.Query(Opts("shared", () => calls++));

        Assert.Same(first, second);
        Assert.Equal(1, calls);

        scope.Dispose();

        Assert.False(Observed(client, "shared"));
    }

    [Fact]
    public void The_Same_Key_At_Two_Sites_Shares_One_Observer_Until_Neither_Holds_It()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var calls = 0;

        var one = scope.Query(("site", 1), Opts("both", () => calls++));
        var two = scope.Query(("site", 2), Opts("both", () => calls++));
        scope.RenderCompleted();

        Assert.Same(one, two);
        Assert.Equal(1, calls);

        scope.Query(("site", 1), Opts("other"));
        scope.Query(("site", 2), Opts("both"));
        scope.RenderCompleted();
        Assert.True(Observed(client, "both"));

        scope.Query(("site", 1), Opts("other"));
        scope.Query(("site", 2), Opts("another"));
        scope.RenderCompleted();
        Assert.False(Observed(client, "both"));
    }

    [Fact]
    public void An_Explicit_Slot_Is_An_Identity_Of_Its_Own()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        foreach (var key in new[] { "a", "b" })
            scope.Query(("rows", key), Opts(key));
        scope.RenderCompleted();

        scope.Query(("rows", "a"), Opts("c"));
        scope.Query(("rows", "b"), Opts("b"));
        scope.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));
        Assert.True(Observed(client, "c"));
    }

    [Fact]
    public void The_Builder_Overloads_Read_Like_The_Options_Ones()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        var byLine = scope.Query(QueryOptions.Create<string>("built", _ => Task.FromResult("built")));
        var bySlot = scope.Query(new object(), QueryOptions.Create<string>("slotted", _ => Task.FromResult("slotted")));

        Assert.NotNull(byLine);
        Assert.NotNull(bySlot);
        Assert.True(Observed(client, "built"));
        Assert.True(Observed(client, "slotted"));
    }

    [Fact]
    public void A_String_Slot_Is_A_Slot_And_Never_A_File_Name()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        scope.Query("text", Opts("a"));
        scope.Query("text", QueryOptions.Create<string>("b", _ => Task.FromResult("b")));
        scope.RenderCompleted();

        scope.Query("text", Opts("b"));
        scope.RenderCompleted();

        Assert.False(Observed(client, "a"));
        Assert.True(Observed(client, "b"));

        scope.Mutation("text", MutationOptions.Create<Req, string>(_ => Task.FromResult("m")));
        scope.Mutation("text", MutationOptions.Create<Req, string>(_ => Task.FromResult("m")).Build());
    }

    [Fact]
    public void A_Key_Read_With_Another_Result_Type_Throws()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        scope.Query(Opts("typed"));

        Assert.Throws<InvalidOperationException>(() =>
            scope.Query(QueryOptions.Create<int>("typed", _ => Task.FromResult(1)).Build()));
    }

    [Fact]
    public async Task A_Repeat_Read_Applies_The_Options_Of_That_Render()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var calls = 0;

        scope.Query("slot", QueryOptions.Create<string>("toggle", _ =>
        {
            calls++;
            return Task.FromResult("d");
        }).Enabled(false));
        Assert.Equal(0, calls);

        var state = scope.Query("slot", QueryOptions.Create<string>("toggle", _ =>
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
        using var scope = client.CreateScope();

        var first = scope.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")).Build());
        scope.RenderCompleted();
        scope.RenderCompleted();

        var again = scope.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("b")).Build());

        Assert.Same(first, again);
    }

    [Fact]
    public async Task A_Repeat_Mutation_Read_Applies_The_New_Options()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        scope.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-1")).Build());
        var state = scope.Mutation("save", MutationOptions.Create<Req, string>(_ => Task.FromResult("render-2")).Build());

        await state.ExecuteAsync(new Req("a"));

        Assert.Equal("render-2", state.Data);
    }

    [Fact]
    public void Mutations_Are_Told_Apart_By_Key_And_By_Call_Site()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        var a = scope.Mutation("a", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")).Build());
        var b = scope.Mutation("b", MutationOptions.Create<Req, string>(_ => Task.FromResult("b")).Build());
        var site = scope.Mutation(MutationOptions.Create<Req, string>(_ => Task.FromResult("c")));
        var builderKey = scope.Mutation("a", MutationOptions.Create<Req, string>(_ => Task.FromResult("a")));

        Assert.NotSame(a, b);
        Assert.NotSame(a, site);
        Assert.Same(a, builderKey);
    }

    [Fact]
    public async Task A_Running_Mutation_Survives_Sweeps_And_Reports_To_The_Host()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var changed = 0;
        using var host = scope.Attach(() => Interlocked.Increment(ref changed));
        var started = Signal();
        var release = Signal();

        var state = scope.Mutation("run", MutationOptions.Create<Req, string>(async _ =>
        {
            started.SetResult();
            await release.Task;
            return "done";
        }).Build());

        var run = state.ExecuteAsync(new Req("a"));
        await started.Task;

        scope.RenderCompleted();
        scope.RenderCompleted();
        Assert.True(state.IsFetching);
        Assert.True(Volatile.Read(ref changed) > 0);

        var before = Volatile.Read(ref changed);
        release.SetResult();
        await run;

        Assert.True(Volatile.Read(ref changed) > before);
        Assert.Equal("done", scope.Mutation("run", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build()).Data);
    }

    [Fact]
    public async Task A_Change_The_Render_Did_Not_Read_Reaches_The_Host()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var gate = Signal();
        var changed = Signal();
        using var host = scope.Attach(() => changed.TrySetResult());

        var state = scope.Query(Gated("slow", gate));

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
        using var scope = client.CreateScope();
        var changed = 0;
        using var host = scope.Attach(() => Interlocked.Increment(ref changed));

        var state = scope.Query(Opts("sync"));

        Assert.True(state.IsResolved);
        Assert.True(changed > 0);

        var settled = changed;
        scope.Query(Opts("sync"));
        Assert.Equal(settled, changed);
    }

    [Fact]
    public async Task A_Change_Before_Any_Host_Is_Delivered_Once_On_Attach()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var state = scope.Mutation("early", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build());

        await state.ExecuteAsync(new Req("a"));

        var changed = 0;
        using var host = scope.Attach(() => changed++);

        Assert.Equal(1, changed);
    }

    [Fact]
    public void Nothing_Is_Delivered_On_Attach_When_Nothing_Changed()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var changed = 0;

        using var host = scope.Attach(() => changed++);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_Second_Host_Is_Refused_Until_The_First_Detaches()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();

        var first = scope.Attach(() => { });

        Assert.Throws<InvalidOperationException>(() => scope.Attach(() => { }));

        first.Dispose();
        scope.Attach(() => { }).Dispose();
    }

    [Fact]
    public async Task A_Detached_Host_Is_Not_Told()
    {
        using var client = NewClient();
        using var scope = client.CreateScope();
        var gate = Signal();
        var changed = 0;

        var link = scope.Attach(() => changed++);
        var state = scope.Query(Gated("late", gate));
        link.Dispose();
        changed = 0;

        gate.SetResult();
        await TestUtils.WaitForStateAsync(state, s => s.IsResolved);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Disposing_The_Scope_Disposes_Every_Observer()
    {
        using var client = NewClient();
        var scope = client.CreateScope();

        scope.Query(Opts("q1"));
        scope.Query(Opts("q2"));
        scope.Mutation("m", MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build());

        scope.Dispose();
        scope.Dispose();

        Assert.False(Observed(client, "q1"));
        Assert.False(Observed(client, "q2"));
    }

    [Fact]
    public async Task After_Dispose_Reads_Throw_And_Everything_Else_Is_Quiet()
    {
        using var client = NewClient();
        var scope = client.CreateScope();
        var gate = Signal();
        var changed = 0;
        var host = scope.Attach(() => changed++);

        var state = scope.Query(Gated("gone", gate));
        scope.Dispose();
        changed = 0;

        Assert.Throws<ObjectDisposedException>(() => scope.Query(Opts("x")));
        Assert.Throws<ObjectDisposedException>(() => scope.Query(new object(), Opts("x")));
        Assert.Throws<ObjectDisposedException>(() =>
            scope.Mutation(MutationOptions.Create<Req, string>(_ => Task.FromResult("x")).Build()));
        Assert.Throws<ObjectDisposedException>(() => scope.Attach(() => { }));

        scope.RenderCompleted();
        host.Dispose();

        gate.SetResult();
        await TestUtils.WaitForStateAsync(state, s => s.IsResolved);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Creating_A_Scope_On_A_Disposed_Client_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.CreateScope());
    }
}
