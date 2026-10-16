using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.TestKit.Tests;

public class ManualEvictionTests
{
    private static QueryOptions<ValueTuple<string>, string> Item(string key) =>
        QueryOptions.Create(key, _ => Task.FromResult("data"));

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Losing_The_Last_Observer_Registers_The_Key()
    {
        var eviction = new ManualEviction();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        var observer = client.Subscribe(Item("a"), () => { });
        Assert.Empty(eviction.Registered);
        observer.Dispose();

        Assert.Equal([ValueTuple.Create("a")], eviction.Registered);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Gaining_An_Observer_Cancels_The_Key()
    {
        var eviction = new ManualEviction();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        client.Subscribe(Item("a"), () => { }).Dispose();
        client.Subscribe(Item("a"), () => { });

        Assert.Contains(ValueTuple.Create("a"), eviction.Cancelled);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Evicting_Removes_The_Query_Before_It_Returns()
    {
        var eviction = new ManualEviction();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);
        client.Subscribe(Item("a"), () => { }).Dispose();
        Assert.NotNull(client.FindQuery("a"));

        eviction.Evict(ValueTuple.Create("a"));

        Assert.Null(client.FindQuery("a"));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Evicting_Raises_The_Event_On_The_Calling_Thread()
    {
        var eviction = new ManualEviction();
        var raisedInline = false;
        eviction.OnEvictionRequired += _ => raisedInline = Inline.Inside;

        Inline.Run(() => eviction.Evict(ValueTuple.Create("a")));

        Assert.True(raisedInline);
    }
}
