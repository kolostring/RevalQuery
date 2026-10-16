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
}
