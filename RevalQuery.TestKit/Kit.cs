using System.Runtime.CompilerServices;
using RevalQuery.Core;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.TestKit;

/// <summary>
/// The kit's free-standing helpers, meant to be imported with <c>using static</c>.
/// </summary>
public static class Kit
{
    /// <summary>
    /// The one timeout in the test suites, in milliseconds, for a test's <c>Timeout</c>:
    /// <c>[Fact(Timeout = HangGuardMilliseconds)]</c>.
    /// </summary>
    /// <remarks>
    /// It turns a hang into a failure and never decides whether a test passes: every wait ends on
    /// a signal the test or the library raises, long before the timeout on any machine. It is
    /// generous so a slow or busy agent never trips it. Waits inside the test take
    /// <c>TestContext.Current.CancellationToken</c>, which the timeout cancels.
    /// </remarks>
    public const int HangGuardMilliseconds = 30_000;

    /// <summary>
    /// <see cref="HangGuardMilliseconds"/> as a <see cref="TimeSpan"/>, for the bUnit wait ceiling
    /// that has no cancellation token.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromMilliseconds(HangGuardMilliseconds);

    /// <summary>
    /// Lets a task run on unawaited, observing its exception so it never surfaces as an
    /// unobserved task exception.
    /// </summary>
    /// <typeparam name="TRes">The task's result type.</typeparam>
    /// <param name="query">The task to let go of.</param>
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

    /// <summary>
    /// The concrete state of the query under a string key, for the members a test waits on that
    /// <c>IQueryState</c> does not expose, such as <c>RestoreCompleted</c>.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="client">The client holding the query.</param>
    /// <param name="key">The query's single-segment key.</param>
    /// <exception cref="InvalidOperationException">No query of that result type is under the key.</exception>
    public static QueryState<ValueTuple<string>, TRes> StateOf<TRes>(this RevalClient client, string key) =>
        client.StateOf<TRes, ValueTuple<string>>(ValueTuple.Create(key));

    /// <summary>
    /// The concrete state of the query under a key, for the members a test waits on that
    /// <c>IQueryState</c> does not expose, such as <c>RestoreCompleted</c>.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <typeparam name="TKey">The key's tuple type.</typeparam>
    /// <param name="client">The client holding the query.</param>
    /// <param name="key">The query's key.</param>
    /// <exception cref="InvalidOperationException">No query of that result type is under the key.</exception>
    public static QueryState<TKey, TRes> StateOf<TRes, TKey>(this RevalClient client, TKey key) where TKey : ITuple =>
        client.FindQuery<TRes, TKey>(key) as QueryState<TKey, TRes>
        ?? throw new InvalidOperationException($"No {typeof(TRes).Name} query under {key}.");

    /// <summary>
    /// <c>GetOrCreateQuery</c>, returning the concrete state for the members a test waits on that
    /// <c>IQueryState</c> does not expose.
    /// </summary>
    /// <typeparam name="TKey">The key's tuple type.</typeparam>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="client">The client to create the query in.</param>
    /// <param name="options">The query's options.</param>
    public static QueryState<TKey, TRes> GetOrCreateState<TKey, TRes>(
        this RevalClient client, QueryOptions<TKey, TRes> options) where TKey : ITuple =>
        (QueryState<TKey, TRes>)client.GetOrCreateQuery(options);
}
