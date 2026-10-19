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

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Recipe_Rolls_Back_To_The_Value_The_Successful_Attempt_Replaced_When_Its_Updater_Reran()
    {
        var first = RenderComponent<TodoList>();
        var second = RenderComponent<TodoList>();
        _fetches.Release(0, ["a"]);
        first.WaitForAssertion(() => Assert.Equal("a", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a", Todos(second)));

        var attempts = 0;
        var options = TodoRecipe.AddTodo(attempt =>
        {
            attempts = attempt;
            // Another set lands while the first attempt runs, so the data moved under it.
            if (attempt == 1) _client.SetQueryData(TodoRecipe.Todos().Build(), c => [.. c!, "other"]);
        });

        var run = _client.CreateMutation(options.Build(), () => { }).State
            .ExecuteAsync("new", Xunit.TestContext.Current.CancellationToken);
        await _adds.Started(0).WaitAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        first.WaitForAssertion(() => Assert.Equal("a,other,new", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a,other,new", Todos(second)));

        _adds.Fail(0, new InvalidOperationException("server said no"));
        await run;

        // The refetch the invalidation starts stays held, so what shows is the rollback: the
        // value the applied attempt replaced, which includes the other set.
        first.WaitForAssertion(() => Assert.Equal("a,other", Todos(first)));
        second.WaitForAssertion(() => Assert.Equal("a,other", Todos(second)));
    }
}
