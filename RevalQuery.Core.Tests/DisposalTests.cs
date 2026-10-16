using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class DisposalTests
{
    private static QueryOptionsBuilder<ValueTuple<string>, string> Options(string key) =>
        QueryOptions.Create(key, static _ => Task.FromResult("data"));

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    [Fact]
    public void Subscribe_After_Disposal_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Subscribe(Options("a").Build(), () => { }));
    }

    [Fact]
    public async Task QueryAsync_After_Disposal_Throws()
    {
        var client = NewClient();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.QueryAsync(Options("c").Build()));
    }

    [Fact]
    public void GetOrCreateQuery_After_Disposal_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.GetOrCreateQuery(Options("d").Build()));
    }

    [Fact]
    public void A_Refused_Subscribe_Leaves_Nothing_Behind()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Subscribe(Options("e").Build(), () => { }));

        Assert.Null(client.FindQuery("e"));
    }

    [Fact]
    public async Task Teardown_After_Disposal_Is_Silent()
    {
        var client = NewClient();
        var observer = client.Subscribe(Options("f").Build(), () => { });
        Assert.True(observer.Query.IsResolved);

        client.Dispose();

        observer.Dispose();
        await client.CancelAsync("f");
        client.Invalidate("f");
        client.Dispose();
    }

    [Fact]
    public async Task Teardown_After_Disposal_With_A_Fetch_In_Flight_Is_Silent()
    {
        var handler = new GatedHandler<string>();
        var client = NewClient();
        var observer = client.Subscribe(QueryOptions.Create("g", handler.Handle).Build(), () => { });
        Assert.Equal(1, handler.Calls);

        client.Dispose();
        await handler.Cancelled(0).WaitAsync(HangGuard);

        observer.Dispose();
        await client.CancelAsync("g").WaitAsync(HangGuard);
        client.Invalidate("g");
        client.Dispose();

        Assert.True(observer.Query.IsPending);
        Assert.Equal(1, handler.Calls);
    }
}
