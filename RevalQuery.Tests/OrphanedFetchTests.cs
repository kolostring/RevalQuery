using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

public class OrphanedFetchTests
{
    // A fetch left running by a released worker must stay reachable from the registry.
    [Fact]
    public async Task CancelAsync_Reaches_A_Fetch_Left_Running_By_A_Released_Worker()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("orphan", async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Dispose();

        var cancelling = client.CancelAsync("orphan");
        release.TrySetResult();
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(client.FindQuery<string>("orphan")!.Data);
    }

    // A released worker must not be replaced by a second one that fetches alongside the first.
    [Fact]
    public async Task A_Released_Worker_Does_Not_Let_A_Second_Fetch_Run_Alongside_Its_Own()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var concurrent = 0;
        var peak = 0;

        var options = QueryOptions.Create<string>("double", async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);

            started.TrySetResult();
            await release.Task;

            Interlocked.Decrement(ref concurrent);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Dispose();

        var fetch = client.QueryAsync(options);
        await Task.Delay(100);

        release.TrySetResult();
        await fetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (seen >= value) return;
        } while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private static QueryClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
}
