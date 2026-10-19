using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Tests;

public class ReviewOfC63a644Tests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Set_Is_Applied_And_Queued_For_Saving_As_One_Step()
    {
        var state = new QueryState<ValueTuple<string>, string>(
            ValueTuple.Create("one-step"), _ => Task.FromResult("unused"), null, null, null);

        var order = new List<string>();
        var bothSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<QueryFreshness, Func<Task>> Saving(string name) => _ => () =>
        {
            lock (order)
            {
                order.Add(name);
                if (order.Count == 2) bothSaved.SetResult();
            }

            return Task.CompletedTask;
        };

        Thread? other = null;

        state.ApplySet("first", null, f =>
        {
            // Runs inside the first set's own step. A second set started now must not land
            // until that step, queueing included, is over.
            other = new Thread(() => state.ApplySet("second", null, Saving("second")));
            other.Start();
            SpinWait.SpinUntil(() => other.ThreadState.HasFlag(ThreadState.WaitSleepJoin) || !other.IsAlive);

            Assert.Equal("first", state.Data);
            return Saving("first")(f);
        });

        await bothSaved.Task.WaitAsync(TestContext.Current.CancellationToken);
        other!.Join();

        Assert.Equal(["first", "second"], order);
        Assert.Equal("second", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_Set_Whose_Data_Moved_Under_Its_Updater_Reruns_The_Updater_On_The_Newer_Data()
    {
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
        var options = QueryOptions.Create("rerun", _ => Task.FromResult("fetched")).Build();
        client.SetQueryData(options, "base");

        var runs = 0;
        client.SetQueryData(options, current =>
        {
            runs++;
            if (runs == 1)
            {
                // A second set lands while this updater is running.
                client.SetQueryData(options, c => c + "-two");
            }

            return current + "-one";
        });

        Assert.Equal(2, runs);
        Assert.Equal("base-two-one", client.FindQuery<string>("rerun")!.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Fetchs_Save_Queues_Ahead_Of_A_Later_Set_In_The_Order_The_Writes_Were_Applied()
    {
        var ct = TestContext.Current.CancellationToken;
        using var persistence = new GatedPersistence();
        using var client = new RevalClient(
            new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), persistence: persistence);
        var handler = new GatedHandler<string>();
        var options = QueryOptions.Create("fetch-queue", handler.Handle).Build();

        var fetch = client.QueryAsync(options, ct);
        await handler.Started(0).WaitAsync(ct);

        // Parks the fetching thread inside the notification that follows the fetch's write, so
        // the set below is applied after the fetch wrote and before the fetch went on. What the
        // fetch does with its save from there is exactly what the fix moved.
        var state = client.FindQuery<string>("fetch-queue")!;
        var parked = new Gate();
        var resume = new Gate();
        var claimed = 0;
        state.OnChanged += () =>
        {
            if (state.Data != "fetched" || Interlocked.Exchange(ref claimed, 1) == 1) return;
            parked.Open();
            resume.Task.Wait(ct);
        };

        handler.Release(0, "fetched");
        await parked.Task.WaitAsync(ct);

        client.SetQueryData(options, "set");
        await persistence.SaveStarted(0).WaitAsync(ct);

        resume.Open();
        await fetch.WaitAsync(ct);
        await persistence.Saved(1).WaitAsync(ct);

        // The registry ended on the set, so the store must too: the fetch's write was applied
        // first, so its save reaches the store first.
        Assert.Equal("set", state.Data);
        var stored = persistence.Saves.Select(save => ((PersistedQuery<string>)save.Entry).Data).ToArray();
        Assert.Equal(["fetched", "set"], stored);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task When_OnMutate_Throws_The_Typed_Exception_And_Settled_Callbacks_Run_With_Default()
    {
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());
        var seen = new ConcurrentQueue<string>();

        var options = MutationOptions.Create<string, string>(_ => Task.FromResult("never"))
            .OnMutate<int>(_ => throw new InvalidOperationException())
            .OnException(ctx => { seen.Enqueue("exception:" + ctx.OnMutateResult); return Task.CompletedTask; })
            .OnSettled(ctx => { seen.Enqueue("settled:" + ctx.OnMutateResult); return Task.CompletedTask; })
            .Build();

        await client.CreateMutation(options, () => { }).State
            .ExecuteAsync("a", TestContext.Current.CancellationToken);

        Assert.Equal(["exception:0", "settled:0"], seen.ToArray());
    }
}
