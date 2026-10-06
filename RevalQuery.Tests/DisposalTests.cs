using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

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
        await Task.Delay(100);

        client.Dispose();

        observer.Dispose();
        await client.CancelAsync("f");
        client.Invalidate("f");
        client.Dispose();
    }
}
