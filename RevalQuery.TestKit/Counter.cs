namespace RevalQuery.TestKit;

/// <summary>
/// A thread-safe count owned by one test. Create one per test, never as a static, so work left
/// over from another test cannot change it. Also the source of unique test data in place of the
/// clock.
/// </summary>
public sealed class Counter
{
    private int _value;

    /// <summary>
    /// The current count.
    /// </summary>
    public int Value => Volatile.Read(ref _value);

    /// <summary>
    /// Adds one and returns the new count.
    /// </summary>
    public int Increment() => Interlocked.Increment(ref _value);
}
