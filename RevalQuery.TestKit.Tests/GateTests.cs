namespace RevalQuery.TestKit.Tests;

public class GateTests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Opening_Completes_The_Task()
    {
        var gate = new Gate();
        Assert.False(gate.Task.IsCompleted);

        gate.Open();

        await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(gate.Task.IsCompletedSuccessfully);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Failing_Faults_The_Task_With_The_Exception()
    {
        var gate = new Gate();
        var boom = new InvalidOperationException("boom");

        gate.Fail(boom);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Same(boom, thrown);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Opening_Does_Not_Run_Continuations_On_The_Opening_Thread()
    {
        var gate = new Gate();
        var ranInline = gate.Task.ContinueWith(_ => Inline.Inside, TaskContinuationOptions.ExecuteSynchronously);

        Inline.Run(gate.Open);

        Assert.False(await ranInline.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Opening_An_Open_Gate_Again_Is_Harmless()
    {
        var gate = new Gate();

        gate.Open();
        gate.Open();

        await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
    }
}
