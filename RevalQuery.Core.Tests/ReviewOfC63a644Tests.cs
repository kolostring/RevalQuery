using RevalQuery.Core.Abstractions.Query;
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
}
