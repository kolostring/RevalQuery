using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Tests;

public static class TestUtils
{
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