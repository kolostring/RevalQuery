using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Scope;

namespace RevalQuery.Blazor.Tests;

/// <summary>
/// Real components rendered by bUnit, with handlers held open by completion sources. Nothing
/// here sleeps: a test waits for the render it expects, and asserts on quiet by flushing the
/// renderer's dispatcher rather than by waiting out a delay.
/// </summary>
public class QueryHostTests : TestContext
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Fx NewFx()
    {
        var fx = new Fx(Services);
        Services.AddSingleton(fx);
        Services.AddSingleton(fx.Client);
        return fx;
    }

    // Everything queued on the dispatcher before this call has run when it returns
    private Task Flush(IRenderedFragment cut) => cut.InvokeAsync(() => { });

    private static string[] Items(IRenderedFragment cut) =>
        cut.FindAll(".item").Select(e => e.TextContent).ToArray();

    private static int WithData(IRenderedFragment cut) => Items(cut).Count(t => t.StartsWith("item"));

    // ---- loading to data ----

    [Fact]
    public void A_Query_Goes_From_Loading_To_Data_Without_The_Page_Asking_To_Render()
    {
        var fx = NewFx();
        var gate = fx.Gate(1);
        var cut = RenderComponent<Basic>(p => p.Add(c => c.Id, 1));

        Assert.Equal("loading", cut.Find("#out").TextContent);

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("item1", cut.Find("#out").TextContent));

        Assert.Equal(1, fx.CallsFor(1));
    }

    [Fact]
    public void A_Query_That_Completes_During_The_Read_Costs_No_Extra_Render()
    {
        var fx = NewFx();
        var cut = RenderComponent<Basic>(p => p.Add(c => c.Id, 1));

        // The handler finished inside Query(), so the notification only described the render
        // already in progress and was not forwarded
        Assert.Equal("item1", cut.Find("#out").TextContent);
        Assert.Equal(1, cut.Instance.Renders);
        Assert.Equal(1, fx.CallsFor(1));
    }

    // ---- release ----

    [Fact]
    public void Switching_Key_Releases_The_Old_Query_And_Keeps_The_New_One()
    {
        var fx = NewFx();
        var cut = RenderComponent<Basic>(p => p.Add(c => c.Id, 1));

        cut.SetParametersAndRender(p => p.Add(c => c.Id, 2));

        Assert.Equal("item2", cut.Find("#out").TextContent);
        Assert.False(fx.Observed(1));
        Assert.True(fx.Observed(2));
        Assert.Equal(1, fx.Eviction.RegisteredCount(1));
        Assert.Equal(0, fx.Eviction.RegisteredCount(2));
    }

    [Fact]
    public void A_Loop_Releases_What_It_Dropped_And_Keeps_The_Rest_Unfetched_Again()
    {
        var fx = NewFx();
        var cut = RenderComponent<Loop>(p => p.Add(c => c.Ids, [1, 2, 3]));

        cut.SetParametersAndRender(p => p.Add(c => c.Ids, [1, 3, 4]));

        Assert.Equal(["item1", "item3", "item4"], Items(cut));
        Assert.True(fx.Observed(1));
        Assert.False(fx.Observed(2));
        Assert.True(fx.Observed(3));
        Assert.True(fx.Observed(4));
        Assert.All(new[] { 1, 2, 3, 4 }, id => Assert.Equal(1, fx.CallsFor(id)));
    }

    [Fact]
    public void A_Helper_Called_Twice_And_Two_Reads_On_One_Line_Each_Keep_Their_Query()
    {
        var fx = NewFx();
        var cut = RenderComponent<Helper>();

        for (var tick = 1; tick <= 4; tick++) cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick));

        Assert.All(new[] { 1, 2, 3, 4 }, id =>
        {
            Assert.True(fx.Observed(id), $"{id} was released");
            Assert.Equal(1, fx.CallsFor(id));
        });
    }

    // ---- reads that happen away from the page's own render ----

    [Fact]
    public async Task Content_Rendered_In_A_Later_Batch_Is_Not_Released_And_Nothing_Refetches()
    {
        var fx = NewFx();
        var cut = RenderComponent<InChild>(p => p
            .Add(c => c.Kind, "popover")
            .Add(c => c.Ids, [1, 2, 3]));

        cut.WaitForAssertion(() => Assert.Equal(3, WithData(cut)));

        for (var tick = 1; tick <= 4; tick++) cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick));
        await Flush(cut);

        Assert.All(new[] { 1, 2, 3 }, id => Assert.True(fx.Observed(id), $"{id} was released"));

        // The page's own query and one per row, each fetched once at StaleTime zero
        Assert.Equal(4, fx.TotalCalls);
    }

    [Fact]
    public async Task Later_Batch_Content_Still_Releases_The_Keys_It_Stops_Reading()
    {
        var fx = NewFx();
        var cut = RenderComponent<InChild>(p => p.Add(c => c.Kind, "popover").Add(c => c.Ids, [1, 2, 3]));
        cut.WaitForAssertion(() => Assert.Equal(3, WithData(cut)));

        // The first render after the change still counts the reads the popover made in its own
        // batch before it, which no sweep has seen: key 2 goes at the render after that.
        cut.SetParametersAndRender(p => p.Add(c => c.Ids, [1, 3]));
        Assert.True(fx.Observed(2));

        cut.SetParametersAndRender(p => p.Add(c => c.Tick, 1));
        await Flush(cut);

        Assert.True(fx.Observed(1));
        Assert.False(fx.Observed(2));
        Assert.True(fx.Observed(3));
    }

    [Fact]
    public async Task Content_A_Child_Hides_While_It_Loads_Is_Not_Released_And_Not_Refetched()
    {
        var fx = NewFx();
        fx.Stale = TimeSpan.Zero;
        var cut = RenderComponent<InChild>(p => p.Add(c => c.Kind, "gated").Add(c => c.Ids, [1, 2, 3]));
        Assert.Equal(3, WithData(cut));
        Assert.Equal(4, fx.TotalCalls);

        var gate = Gate();
        for (var tick = 1; tick <= 3; tick++)
            cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick).Add(c => c.Gate, gate.Task));

        // The child is showing nothing, so nothing read the rows, and nothing was released
        Assert.Empty(Items(cut));
        Assert.All(new[] { 1, 2, 3 }, id => Assert.True(fx.Observed(id)));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal(3, WithData(cut)));
        await Flush(cut);

        Assert.All(new[] { 1, 2, 3 }, id => Assert.True(fx.Observed(id)));
        Assert.Equal(4, fx.TotalCalls);
        Assert.All(new[] { 1, 2, 3 }, id => Assert.Equal(0, fx.Eviction.RegisteredCount(id)));
    }

    // ---- known limitation ----

    [Fact]
    public void A_Hidden_Branch_Keeps_Its_Query_Until_It_Renders_Again()
    {
        var fx = NewFx();
        var cut = RenderComponent<Hidden>();

        cut.SetParametersAndRender(p => p.Add(c => c.Show, false));
        for (var tick = 1; tick <= 3; tick++) cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick));

        // Documented: nothing says the branch will not come back, so its query stays
        Assert.Empty(cut.FindAll(".item"));
        Assert.True(fx.Observed(1));

        cut.SetParametersAndRender(p => p.Add(c => c.Show, true));

        Assert.Equal("item1", cut.Find(".item").TextContent);
        Assert.Equal(1, fx.CallsFor(1));
    }

    // ---- mutations ----

    [Fact]
    public async Task A_Running_Mutation_Survives_Renders_And_Its_State_Reaches_The_Page()
    {
        NewFx();
        var gate = Gate();
        var cut = RenderComponent<Saver>(p => p.Add(c => c.Gate, gate.Task));
        var state = cut.Instance.Last!;
        Assert.Equal("Idle", cut.Find(".status").TextContent);

        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Equal("Fetching", cut.Find(".status").TextContent));

        // Nothing reads it while it runs, through renders that each complete a sweep
        cut.SetParametersAndRender(p => p.Add(c => c.Show, false));
        for (var tick = 1; tick <= 3; tick++) cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick));

        gate.SetResult();
        await TestUtils.UntilAsync(() => state.IsResolved);
        cut.SetParametersAndRender(p => p.Add(c => c.Show, true));

        Assert.Same(state, cut.Instance.Last);
        Assert.Equal("Resolved", cut.Find(".status").TextContent);
        Assert.Equal("saved", cut.Find(".data").TextContent);
    }

    [Fact]
    public void A_Mutation_Runs_With_The_Options_Of_The_Latest_Render()
    {
        NewFx();
        var cut = RenderComponent<Saver>(p => p.Add(c => c.Label, "first"));

        cut.SetParametersAndRender(p => p.Add(c => c.Label, "second"));
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Equal("second", cut.Find(".data").TextContent));
    }

    // ---- host ----

    [Fact]
    public void Disposing_The_Page_Disposes_The_Scope_And_Releases_Every_Query()
    {
        var fx = NewFx();
        var cut = RenderComponent<Loop>(p => p.Add(c => c.Ids, [1, 2, 3]));

        DisposeComponents();

        Assert.All(new[] { 1, 2, 3 }, id =>
        {
            Assert.False(fx.Observed(id));
            Assert.Equal(1, fx.Eviction.RegisteredCount(id));
        });
    }

    [Fact]
    public void A_Host_Without_A_Scope_Says_So()
    {
        NewFx();

        var error = Assert.ThrowsAny<Exception>(() => RenderComponent<QueryHost>());

        Assert.Contains("requires a Scope", error.ToString());
    }

    [Fact]
    public void A_Scope_Made_Without_An_Owner_Is_Refused_With_The_Fix()
    {
        var fx = NewFx();
        using var scope = fx.Client.CreateScope();

        var error = Assert.ThrowsAny<Exception>(() => RenderComponent<QueryHost>(p => p.Add(c => c.Scope, scope)));

        Assert.Contains("CreateScope(this)", error.ToString());
    }

    [Fact]
    public void An_Owner_Without_ComponentBase_Is_Re_Rendered_Through_Its_Own_Handler()
    {
        var fx = NewFx();
        var gate = fx.Gate(1);
        var cut = RenderComponent<HandOwner>();

        cut.Instance.Scope!.Query(fx.Detail(1));
        Assert.Equal(0, cut.Instance.Events);

        gate.SetResult();

        cut.WaitForState(() => cut.Instance.Events > 0);
        Assert.Equal(1, cut.Instance.Events);
    }

    /// <summary>
    /// An owner that implements the handler itself and derives from nothing. It renders its host
    /// in <see cref="HandleEventAsync"/>, as a component that cares about events would.
    /// </summary>
    private sealed class HandOwner : IComponent, IHandleEvent
    {
        private RenderHandle _handle;

        [Inject] public QueryClient Client { get; set; } = default!;

        public QueryScope? Scope { get; private set; }
        public int Events;

        public void Attach(RenderHandle renderHandle) => _handle = renderHandle;

        public Task SetParametersAsync(ParameterView parameters)
        {
            parameters.SetParameterProperties(this);
            Scope ??= Client.CreateScope(this);
            Render();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventCallbackWorkItem item, object? arg)
        {
            Interlocked.Increment(ref Events);
            Render();
            return Task.CompletedTask;
        }

        private void Render() => _handle.Render(builder =>
        {
            builder.OpenComponent<QueryHost>(0);
            builder.AddComponentParameter(1, nameof(QueryHost.Scope), Scope);
            builder.CloseComponent();
        });
    }
}

internal static class TestUtils
{
    /// <summary>Waits for a condition a notification cannot be awaited on, bounded only so a failure ends.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
