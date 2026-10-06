using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Blazor.Tests;

public class RevalRendererTests : TestContext
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Fx NewFx()
    {
        var fx = new Fx(Services);
        Services.AddSingleton(fx);
        Services.AddSingleton(fx.Client);
        Services.AddTransient(_ => fx.Client.CreateHooks());
        return fx;
    }

    private Task Flush(IRenderedFragment cut) => cut.InvokeAsync(() => { });

    private static string[] Items(IRenderedFragment cut) =>
        cut.FindAll(".item").Select(e => e.TextContent).ToArray();

    private static int WithData(IRenderedFragment cut) => Items(cut).Count(t => t.StartsWith("item"));

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
    public async Task A_Query_That_Completes_During_The_Read_Costs_One_Extra_Render_That_Settles()
    {
        var fx = NewFx();
        var cut = RenderComponent<Basic>(p => p.Add(c => c.Id, 1));
        await Flush(cut);

        Assert.Equal("item1", cut.Find("#out").TextContent);
        var renders = cut.Instance.Renders;
        Assert.InRange(renders, 1, 2);

        await Flush(cut);
        await Flush(cut);

        Assert.Equal(renders, cut.Instance.Renders);
        Assert.Equal(1, fx.CallsFor(1));
    }

    [Fact]
    public async Task A_Read_In_A_Later_Batch_That_Changes_State_Re_Renders_The_Page()
    {
        var fx = NewFx();
        var cut = RenderComponent<LateReader>();

        cut.WaitForAssertion(() => Assert.Equal("item1", cut.Find("#own").TextContent));
        await Flush(cut);

        var renders = cut.Instance.Renders;
        await Flush(cut);
        await Flush(cut);

        Assert.Equal(renders, cut.Instance.Renders);
        Assert.Equal(1, fx.CallsFor(1));
    }

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

        Assert.Equal(4, fx.TotalCalls);
    }

    [Fact]
    public async Task Later_Batch_Content_Still_Releases_The_Keys_It_Stops_Reading()
    {
        var fx = NewFx();
        var cut = RenderComponent<InChild>(p => p.Add(c => c.Kind, "popover").Add(c => c.Ids, [1, 2, 3]));
        cut.WaitForAssertion(() => Assert.Equal(3, WithData(cut)));

        cut.SetParametersAndRender(p => p.Add(c => c.Ids, [1, 3]));

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

        Assert.Empty(Items(cut));
        Assert.All(new[] { 1, 2, 3 }, id => Assert.True(fx.Observed(id)));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal(3, WithData(cut)));
        await Flush(cut);

        Assert.All(new[] { 1, 2, 3 }, id => Assert.True(fx.Observed(id)));
        Assert.Equal(4, fx.TotalCalls);
        Assert.All(new[] { 1, 2, 3 }, id => Assert.Equal(0, fx.Eviction.RegisteredCount(id)));
    }

    [Fact]
    public void A_Hidden_Branch_Keeps_Its_Query_Until_It_Renders_Again()
    {
        var fx = NewFx();
        var cut = RenderComponent<Hidden>();

        cut.SetParametersAndRender(p => p.Add(c => c.Show, false));
        for (var tick = 1; tick <= 3; tick++) cut.SetParametersAndRender(p => p.Add(c => c.Tick, tick));

        Assert.Empty(cut.FindAll(".item"));
        Assert.True(fx.Observed(1));

        cut.SetParametersAndRender(p => p.Add(c => c.Show, true));

        Assert.Equal("item1", cut.Find(".item").TextContent);
        Assert.Equal(1, fx.CallsFor(1));
    }

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

    [Fact]
    public void Disposing_The_Page_Releases_The_Hooks_And_Every_Query()
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
    public void Swapping_The_Hooks_Releases_The_Old_Ones_And_Their_Queries()
    {
        var fx = NewFx();
        var cut = RenderComponent<Swapper>(p => p.Add(c => c.Id, 1));
        Assert.True(fx.Observed(1));

        cut.SetParametersAndRender(p => p.Add(c => c.Id, 2));

        Assert.False(fx.Observed(1));
        Assert.True(fx.Observed(2));
        Assert.Equal(1, fx.Eviction.RegisteredCount(1));
    }

    [Fact]
    public async Task A_Renderer_Inside_Child_Content_Re_Renders_The_Owning_Component()
    {
        var fx = NewFx();
        var gate = fx.Gate(1);
        var cut = RenderComponent<Wrapped>(p => p.Add(c => c.Id, 1));

        Assert.Equal("loading", cut.Find("#out").TextContent);

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("item1", cut.Find("#out").TextContent));
        await Flush(cut);

        Assert.Equal(1, fx.CallsFor(1));
    }

    [Fact]
    public void A_Renderer_Without_A_Component_Says_So()
    {
        var fx = NewFx();
        var hooks = fx.Client.CreateHooks();

        var error = Assert.ThrowsAny<Exception>(() => RenderComponent<RevalRenderer>(p => p.Add(c => c.Hooks, hooks)));

        Assert.Contains("requires a Component", error.ToString());
    }

    [Fact]
    public void A_Renderer_Without_Hooks_Says_So()
    {
        NewFx();
        var owner = new HandOwner();

        var error = Assert.ThrowsAny<Exception>(() => RenderComponent<RevalRenderer>(p => p.Add(c => c.Component, owner)));

        Assert.Contains("requires Hooks", error.ToString());
    }

    [Fact]
    public void An_Owner_Without_ComponentBase_Is_Re_Rendered_Through_Its_Own_Handler()
    {
        var fx = NewFx();
        var gate = fx.Gate(1);
        var cut = RenderComponent<HandOwner>();

        cut.Instance.Hooks!.Query(fx.Detail(1));
        cut.WaitForState(() => cut.Instance.Events > 0);
        var events = cut.Instance.Events;

        gate.SetResult();

        cut.WaitForState(() => cut.Instance.Events > events);
    }

    private sealed class HandOwner : IComponent, IHandleEvent
    {
        private RenderHandle _handle;

        [Inject] public RevalClient Client { get; set; } = default!;

        public RevalHooks? Hooks { get; private set; }
        public int Events;

        public void Attach(RenderHandle renderHandle) => _handle = renderHandle;

        public Task SetParametersAsync(ParameterView parameters)
        {
            parameters.SetParameterProperties(this);
            Hooks ??= Client.CreateHooks();
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
            builder.OpenComponent<RevalRenderer>(0);
            builder.AddComponentParameter(1, nameof(RevalRenderer.Component), this);
            builder.AddComponentParameter(2, nameof(RevalRenderer.Hooks), Hooks);
            builder.CloseComponent();
        });
    }
}

internal static class TestUtils
{
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
