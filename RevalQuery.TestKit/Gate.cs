namespace RevalQuery.TestKit;

/// <summary>
/// A one-shot signal the test opens or fails. Whatever awaits it resumes on the thread pool,
/// never inside <see cref="Open"/> or <see cref="Fail"/>, so opening a gate never runs library
/// code on the test's stack.
/// </summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the gate is opened, or faults when it is failed.
    /// </summary>
    public Task Task => _source.Task;

    /// <summary>
    /// Opens the gate. Opening it again, or after it failed, does nothing.
    /// </summary>
    public void Open() => _source.TrySetResult();

    /// <summary>
    /// Faults the gate with <paramref name="exception"/>. Does nothing once the gate is open or failed.
    /// </summary>
    /// <param name="exception">The exception awaiting the gate throws.</param>
    public void Fail(Exception exception) => _source.TrySetException(exception);
}
