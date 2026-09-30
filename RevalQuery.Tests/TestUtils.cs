using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Tests;

public static class TestUtils
{
    /// <summary>
    /// Waits for anything a notification cannot be awaited on, by polling until it holds.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="WaitForStateAsync"/> when the thing waited for is a state property.
    /// This is for what sits beside one: a notification count, a call count, a flag a handler
    /// sets. A fixed delay in their place is a test that fails on a busy machine.
    /// </remarks>
    public static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    public static async Task WaitForStateAsync(IQueryState state, Func<IQueryState, bool> predicate, int timeoutMs = 2000)
    {
        if (predicate(state)) return;

        var tcs = new TaskCompletionSource<bool>();
        void handler() { if (predicate(state)) tcs.TrySetResult(true); }

        state.OnChanged += handler;

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally
        {
            state.OnChanged -= handler;
        }
    }
}