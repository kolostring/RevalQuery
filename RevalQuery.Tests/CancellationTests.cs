using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Tests;

/// <summary>
/// What cancelling a query means: the fetch stops, its result is dropped even when the handler
/// never read its token, and the caller knows when both have happened.
/// </summary>
public class CancellationTests
{
    /// <summary>
    /// Rounds the eviction stress runs for. The race is a narrow window, so it is hit by
    /// repetition rather than by arranging it.
    /// </summary>
    private const int StormRounds = 300;

    [Fact]
    public async Task A_Handler_That_Ignored_Its_Token_Has_Its_Result_Dropped()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        // No token anywhere in the handler, which is the case this test exists for. A query
        // written like this used to apply its result long after the cancellation, because
        // cancelling only signalled a token nothing was reading.
        var options = QueryOptions.Create<string>("ignored", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        // The low-level trigger, which returns without waiting, so the write below happens
        // while the handler is still running. That is the race: the handler finishes later and
        // its result would land on top.
        observer.Query.Cancel();
        observer.Query.Data = "optimistic";

        await Task.Delay(400);

        Assert.Equal("optimistic", observer.Query.Data);
        Assert.True(observer.Query.IsIdle);
        Assert.False(observer.Query.IsException);
    }

    [Fact]
    public async Task CancelAsync_Completes_Only_Once_The_Fetch_Has_Unwound()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("unwind", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        var observer = client.Subscribe(options, () => { });
        await started.Task;

        await client.CancelAsync("unwind");

        // No delay and no polling in between. The moment the await returns, the fetch is over
        // and nothing it produced can still land, which is what makes this usable before an
        // optimistic update.
        Assert.True(observer.Query.IsIdle);
        Assert.Null(observer.Query.Data);
    }

    [Fact]
    public async Task CancelAsync_Cancels_Every_Query_Under_The_Prefix()
    {
        using var client = NewClient();

        var started = new CountdownEvent(2);

        static QueryOptions<(string, string), string> Options(string id, CountdownEvent started) =>
            QueryOptions.Create<(string, string), string>(("users", id), async _ =>
            {
                started.Signal();
                await Task.Delay(200);
                return "from-network";
            }).Build();

        var first = client.Subscribe(Options("a", started), () => { });
        var second = client.Subscribe(Options("b", started), () => { });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        // The prefix, not either key. CancelAsync covers the same set as Invalidate, so the
        // two can be used on the same key.
        await client.CancelAsync(ValueTuple.Create("users"));

        Assert.True(first.Query.IsIdle);
        Assert.True(second.Query.IsIdle);
        Assert.Null(first.Query.Data);
        Assert.Null(second.Query.Data);
    }

    [Fact]
    public async Task A_Cancelled_Await_Abandons_The_Wait_And_Keeps_The_Fetch()
    {
        using var client = NewClient();

        var started = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("abandoned", async _ =>
        {
            started.TrySetResult();
            await Task.Delay(200);
            return "from-network";
        }).Build();

        using var cts = new CancellationTokenSource();
        var fetch = client.FetchQueryAsync(options, cts.Token);

        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);

        // The caller walked away; the fetch did not. Cancelling it too would throw away the
        // cache entry that makes the next keystroke, or the backspace after it, instant.
        await WaitUntil(() => client.FindQuery<string>("abandoned")?.Data is not null);
        Assert.Equal("from-network", client.FindQuery<string>("abandoned")!.Data);
    }

    [Fact]
    public async Task A_Cancelled_Fetch_Does_Not_Throw_An_Older_Fetch_Failure()
    {
        using var client = NewClient();

        var calls = 0;
        var secondStarted = new TaskCompletionSource();

        var options = QueryOptions.Create<string>("stale-error", async ctx =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("first");

            secondStarted.TrySetResult();
            await Task.Delay(5000, ctx.CancellationToken ?? CancellationToken.None);
            return "from-network";
        }).ConfigureRetry(retry => retry.Retry(0)).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchQueryAsync(options));

        // The failure is still on the query, because nothing clears it until a fetch succeeds.
        Assert.True(client.FindQuery<string>("stale-error")!.IsException);

        var second = client.FetchQueryAsync(options);
        await secondStarted.Task;
        await client.CancelAsync("stale-error");

        // This call was cancelled. Reporting the earlier call's exception would tell the
        // caller about an error its own fetch never hit.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task An_Eviction_Between_Run_Attempts_Never_Strands_A_Worker_On_A_Stateless_Node()
    {
        // Evicts the instant a query is left unobserved, which is the window a one-off fetch
        // releases itself in. A real TTL collector reaches the same state, just rarely.
        var eviction = new EvictOnRelease();
        using var client = new QueryClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), eviction);

        var options = QueryOptions.Create<string>("raced", async _ =>
        {
            await Task.Delay(5);
            return "from-network";
        }).Build();

        // Two things at once, which is what the defect needs. The storm is one-off fetches,
        // which own no observer, so each release disposes the worker and a caller that took it
        // a moment earlier gets NotRun and retries. The eviction lands in that retry.
        var rounds = 0;
        var storm = Task.Run(async () =>
        {
            while (Volatile.Read(ref rounds) < StormRounds)
            {
                var fetches = Enumerable.Range(0, 8)
                    .Select(_ => Settle(client.FetchQueryAsync(options)))
                    .ToArray();

                // An eviction this eager can dispose a worker out from under a fetch, so a
                // cancelled one is a fair outcome. Returning null while reporting success is
                // not: that is a caller handed the data of a query it was never bound to.
                foreach (var result in await Task.WhenAll(fetches))
                {
                    if (result is not null) Assert.Equal("from-network", result);
                }
            }
        });

        // Subscribing is what makes the defect bite. A node left holding a worker and no state
        // hands the next subscriber a second state for the same key, driven by nothing, while
        // the worker it was given goes on fetching into the first. The subscriber waits forever.
        try
        {
            while (Interlocked.Increment(ref rounds) < StormRounds)
            {
                var observer = client.Subscribe(options, () => { });

                try
                {
                    await WaitUntil(() => observer.Query.Data is not null, timeoutMs: 1000);
                    Assert.Equal("from-network", observer.Query.Data);
                    AssertEveryWorkerHasItsState(client);
                }
                finally
                {
                    observer.Dispose();
                }
            }
        }
        finally
        {
            Volatile.Write(ref rounds, StormRounds);
            await storm;
        }
    }

    /// <summary>
    /// Runs a fetch to its conclusion, reporting a cancelled one as null rather than throwing.
    /// </summary>
    private static async Task<string?> Settle(Task<string> fetch)
    {
        try
        {
            return await fetch;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ---------- helpers ----------

    private static QueryClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    private sealed class EvictOnRelease : ICacheEvictionPolicy
    {
        public event Action<ITuple>? OnEvictionRequired;

        public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) =>
            OnEvictionRequired?.Invoke(key);

        public void CancelEviction(ITuple key) { }

        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Sampled between rounds, when nothing is mid-registration. A node holding a worker and no
    /// state is the shape the defect leaves behind.
    /// </summary>
    private static void AssertEveryWorkerHasItsState(QueryClient client)
    {
        var registry = (QueryRegistry)typeof(QueryClient)
            .GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;

        Walk(registry.Root);

        static void Walk(RegistryNode node)
        {
            Assert.False(
                node.Worker is not null && node.State is null,
                "a registry node holds a worker but no state, so the next subscriber to that " +
                "key would get a state nothing is driving");

            foreach (var child in node.Children.Values) Walk(child);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }
}
