using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Tests;

public static class TestUtils
{
    public static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    public static void Discard<TRes>(Task<TRes> query)
    {
        _ = Swallow(query);

        static async Task Swallow(Task<TRes> task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }
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