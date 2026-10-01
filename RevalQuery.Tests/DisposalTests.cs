using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// Covers the asymmetric disposal contract: the entry points a live render reaches throw once
/// the client is disposed, while teardown stays a silent no-op because Blazor does not specify
/// whether component disposal runs before or after the DI scope that owns the client.
/// </summary>
public class DisposalTests
{
    private static QueryOptionsBuilder<ValueTuple<string>, string> Options(string key) =>
        QueryOptions.Create(key, static _ => Task.FromResult("data"));

    private static QueryClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    [Fact]
    public void Subscribe_After_Disposal_Throws()
    {
        var client = NewClient();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Subscribe(Options("a").Build(), () => { }));
    }

    // Was two tests, one per imperative method. The collapse leaves one method, and with it
    // one error channel: QueryAsync is async, so a disposed client faults the task it returns
    // rather than throwing from the call. A caller discarding the task sees this only through
    // the catch it writes around the discard.
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

        // The point of throwing: the registry stays empty rather than gaining a state and a
        // worker that nothing is left to dispose.
        Assert.Null(client.FindQuery("e"));
    }

    [Fact]
    public async Task Teardown_After_Disposal_Is_Silent()
    {
        var client = NewClient();
        var observer = client.Subscribe(Options("f").Build(), () => { });
        await Task.Delay(100);

        client.Dispose();

        // Component disposal may land after the scope that owns the client.
        observer.Dispose();
        await client.CancelAsync("f");
        client.Invalidate("f");
        client.Dispose();
    }
}
