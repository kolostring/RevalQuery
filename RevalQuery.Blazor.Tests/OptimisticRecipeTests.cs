using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Hooks;
using RevalQuery.TestKit;

namespace RevalQuery.Blazor.Tests;

public class OptimisticRecipeTests : Bunit.TestContext
{
    private readonly RevalClient _client;
    private readonly GatedHandler<List<string>> _fetches = new();
    private readonly GatedHandler<string> _adds = new();

    public OptimisticRecipeTests()
    {
        Services.AddSingleton(_fetches);
        Services.AddSingleton(_adds);
        _client = new RevalClient(Services.BuildServiceProvider(), new RevalQueryOptions(), new RecordingEviction());
        Services.AddSingleton(_client);
        Services.AddTransient(_ => _client.CreateHooks());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _client.Dispose();
        base.Dispose(disposing);
    }

    private static string Todos(IRenderedFragment cut) => cut.Find(".todos").TextContent;

    private (IRenderedComponent<TodoList> First, IRenderedComponent<TodoList> Second, IRenderedComponent<TodoAdder> Adder) Loaded()
    {
        var first = RenderComponent<TodoList>();
        var second = RenderComponent<TodoList>();
        var adder = RenderComponent<TodoAdder>();

        _fetches.Release(0, ["a"]);
        first.WaitForAssertion(() => Assert.Equal("a", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a", Todos(second)));
        return (first, second, adder);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Recipe_In_One_Component_Updates_Every_Component_Watching_The_Key()
    {
        var (first, second, adder) = Loaded();

        adder.Find("button").Click();

        first.WaitForAssertion(() => Assert.Equal("a,new", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a,new", Todos(second)));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void The_Recipe_Rolls_Every_Component_Back_When_The_Handler_Fails()
    {
        var (first, second, adder) = Loaded();

        adder.Find("button").Click();
        first.WaitForAssertion(() => Assert.Equal("a,new", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a,new", Todos(second)));

        _adds.Fail(0, new InvalidOperationException("server said no"));

        // The refetch the invalidation starts is call 1 and stays held, so what shows is the rollback.
        first.WaitForAssertion(() => Assert.Equal("a", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a", Todos(second)));
    }
}
