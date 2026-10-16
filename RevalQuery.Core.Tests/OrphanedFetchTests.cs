using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class OrphanedFetchTests
{
    [Fact]
    public async Task CancelAsync_Reaches_A_Fetch_Left_Running_By_A_Released_Worker()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>(honourToken: false);

        try
        {
            var observer = client.Subscribe(QueryOptions.Create("orphan", handler.Handle).Build(), () => { });
            Assert.Equal(1, handler.Calls);

            observer.Dispose();

            var cancelling = client.CancelAsync("orphan");
            Assert.False(cancelling.IsCompleted);

            handler.Release(0, "from-network");
            await cancelling.WaitAsync(HangGuard);

            Assert.Null(client.FindQuery<string>("orphan")!.Data);
        }
        finally
        {
            handler.ReleaseAll("released");
        }
    }

    /// <remarks>
    /// The library calls the handler synchronously inside <c>QueryAsync</c>, so a second fetch,
    /// through the released worker or a new one, would show in <c>Calls</c> before
    /// <c>QueryAsync</c> returns. If the handler call ever moves behind a yield, revisit this test.
    /// </remarks>
    [Fact]
    public async Task A_Released_Worker_Does_Not_Let_A_Second_Fetch_Run_Alongside_Its_Own()
    {
        using var client = NewClient();
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("double", handler.Handle).Build();

        var observer = client.Subscribe(options, () => { });
        Assert.Equal(1, handler.Calls);

        observer.Dispose();

        var fetch = client.QueryAsync(options);
        Assert.Equal(1, handler.Calls);
        Assert.False(fetch.IsCompleted);

        handler.Release(0, "from-network");
        await fetch.WaitAsync(HangGuard);

        Assert.Equal(1, handler.Calls);
    }

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
}
