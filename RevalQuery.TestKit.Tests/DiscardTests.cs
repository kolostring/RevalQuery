namespace RevalQuery.TestKit.Tests;

public class DiscardTests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public void Discarding_A_Faulted_Task_Does_Not_Throw()
    {
        Discard(Task.FromException<int>(new InvalidOperationException("boom")));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Discarding_A_Task_That_Faults_Later_Does_Not_Throw()
    {
        var gate = new Gate();

        Discard(Faulting(gate));
        gate.Open();

        await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> Faulting(Gate gate)
    {
        await gate.Task;
        throw new InvalidOperationException("boom");
    }
}
