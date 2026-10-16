using RevalQuery.Core.Abstractions.Mutation;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.TestKit;

/// <summary>
/// Waits on the change notifications the library already raises, in place of polling or
/// sleeping.
/// </summary>
/// <remarks>
/// <para>A wait subscribes to <c>OnChanged</c> first, then checks its condition, then checks again
/// on every change, so it cannot miss a change raised between subscribing and checking. It
/// resumes on the thread pool, never inside the notification that satisfied it, and it fails
/// with a <see cref="TimeoutException"/> at <see cref="Kit.HangGuard"/> rather than returning
/// as though the condition held.</para>
/// <para><c>OnChanged</c> handlers run in the order they subscribed. Create the wait
/// <b>before</b> releasing the gate that leads to the change: a wait created afterwards may
/// subscribe too late to see a state the query passes through, and its first check sees only
/// where the query ended up.</para>
/// </remarks>
public static class Signals
{
    /// <summary>
    /// Completes once <paramref name="condition"/> holds for the query <paramref name="state"/>.
    /// </summary>
    /// <typeparam name="TState">The query state's type.</typeparam>
    /// <param name="state">The query state to watch.</param>
    /// <param name="condition">What the test waits for. Checked on the thread raising each change.</param>
    /// <exception cref="TimeoutException">The condition did not hold within the hang guard.</exception>
    public static Task UntilAsync<TState>(TState state, Func<TState, bool> condition)
        where TState : IObservableQueryState =>
        UntilAsync(state, condition, Kit.HangGuard);

    /// <summary>
    /// Completes once <paramref name="condition"/> holds for the mutation <paramref name="state"/>.
    /// </summary>
    /// <typeparam name="TResponse">The mutation's response type.</typeparam>
    /// <param name="state">The mutation state to watch.</param>
    /// <param name="condition">What the test waits for. Checked on the thread raising each change.</param>
    /// <exception cref="TimeoutException">The condition did not hold within the hang guard.</exception>
    public static Task UntilAsync<TResponse>(
        IMutationState<TResponse> state,
        Func<IMutationState<TResponse>, bool> condition) =>
        WaitAsync(h => state.OnChanged += h, h => state.OnChanged -= h, () => condition(state), Kit.HangGuard);

    internal static Task UntilAsync<TState>(TState state, Func<TState, bool> condition, TimeSpan guard)
        where TState : IObservableQueryState =>
        WaitAsync(h => state.OnChanged += h, h => state.OnChanged -= h, () => condition(state), guard);

    private static async Task WaitAsync(Action<Action> subscribe, Action<Action> unsubscribe, Func<bool> condition, TimeSpan guard)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action check = () =>
        {
            try
            {
                if (condition()) met.TrySetResult();
            }
            catch (Exception exception)
            {
                met.TrySetException(exception);
            }
        };

        subscribe(check);
        try
        {
            check();
            await met.Task.WaitAsync(guard);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The condition did not hold within the hang guard ({guard}).");
        }
        finally
        {
            unsubscribe(check);
        }
    }
}
