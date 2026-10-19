using Bunit;

namespace RevalQuery.Blazor.Tests;

public class PersistentStateHarnessTests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task What_A_Persisting_Callback_Writes_Is_Restored_On_The_Other_Side()
    {
        using var server = await PersistentStateHarness.CreateEmptyAsync();
        server.State.RegisterOnPersisting(() =>
        {
            server.State.PersistAsJson("greeting", "hello");
            return Task.CompletedTask;
        });

        var written = await server.PersistAsync();

        using var client = await PersistentStateHarness.CreateFromAsync(written);

        Assert.True(client.State.TryTakeFromJson<string>("greeting", out var greeting));
        Assert.Equal("hello", greeting);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Persisting_With_Nothing_Registered_Fails()
    {
        using var server = await PersistentStateHarness.CreateEmptyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(server.PersistAsync);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Persisting_Callback_That_Throws_Fails_The_Persist()
    {
        using var server = await PersistentStateHarness.CreateEmptyAsync();
        server.State.RegisterOnPersisting(() => throw new FormatException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(server.PersistAsync);

        Assert.IsType<FormatException>(error.InnerException);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Bunit_Waits_Time_Out_Only_On_The_Hang_Guard() =>
        Assert.Equal(HangGuard, TestContextBase.DefaultWaitTimeout);
}
