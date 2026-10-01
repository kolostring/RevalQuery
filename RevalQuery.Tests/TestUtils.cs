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

    /// <summary>
    /// Starts a query whose result nobody wants, the way a consumer now writes what
    /// <c>PrefetchQuery</c> used to do for them.
    /// </summary>
    /// <remarks>
    /// ADR 0008 removed the library's version of this deliberately: it was
    /// <c>QueryAsync</c> plus a swallow, and a sanctioned helper is the method family growing
    /// back under a new name. So this lives in the test project, as consumer code. The catch
    /// is the part that cannot be skipped — a bare discard leaves a faulted task unobserved.
    /// </remarks>
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
                // Nobody is waiting for this, and the query carries whatever the attempt left
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