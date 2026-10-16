namespace RevalQuery.TestKit;

/// <summary>
/// The kit's free-standing helpers, meant to be imported with <c>using static</c>.
/// </summary>
public static class Kit
{
    /// <summary>
    /// The one timeout in the test suites. Use it only as <c>.WaitAsync(HangGuard)</c>, to turn a
    /// wait that would hang into a failure.
    /// </summary>
    /// <remarks>
    /// It never decides whether a test passes: every wait it guards ends on a signal the test or
    /// the library raises, long before the guard on any machine. It is generous so a slow or busy
    /// agent never trips it.
    /// </remarks>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

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
