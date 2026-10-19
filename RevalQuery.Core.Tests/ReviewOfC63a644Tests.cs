using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task A_Fetchs_Save_Queues_Behind_An_Earlier_Set_In_The_Same_Step_As_Its_Write()
    {
        var state = new QueryState<ValueTuple<string>, string>(
            ValueTuple.Create("fetch-queue"), _ => Task.FromResult("unused"), null, null, null);

        var order = new List<string>();
        var setGate = new Gate();
        var fetchStarted = false;

        state.ApplySet("set", null, _ => async () =>
        {
            await setGate.Task;
            order.Add("set");
        });

        var fetchSaved = state.ApplyFetched("fetched", _ => () =>
        {
            fetchStarted = true;
            order.Add("fetched");
            return Task.CompletedTask;
        });

        Assert.False(fetchStarted);

        setGate.Open();
        await fetchSaved.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["set", "fetched"], order);
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
