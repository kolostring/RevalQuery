namespace RevalQuery.Core.Query;

/// <summary>
/// What the registry needs from the worker driving a query, beyond disposing it.
/// </summary>
/// <remarks>
/// <para>A registry node holds workers of every result type at once, so it cannot name
/// <see cref="QueryWorker{TKey, TRes}"/>. This is the non-generic face of one.</para>
/// <para>Disposing a worker does not stop the fetch it has in flight. Cancelling is a separate
/// request, made by one of the two methods below.</para>
/// </remarks>
internal interface IQueryWorker : IDisposable
{
    /// <summary>
    /// Requests cancellation of the fetch this worker has in flight, if any, and returns
    /// without waiting for it to unwind.
    /// </summary>
    void CancelCurrentFetch();

    /// <summary>
    /// Asks whether the worker can be let go now.
    /// </summary>
    /// <remarks>
    /// False while a fetch is in flight, because the registry node is the only thing pointing
    /// at that fetch: dropping the worker would put it beyond the reach of
    /// <see cref="CancelCurrentFetch"/> and let the next caller start a second one beside it.
    /// The worker remembers the request and raises <see cref="OnReleaseDue"/> once the fetch
    /// settles.
    /// </remarks>
    /// <returns>True when the caller may dispose the worker.</returns>
    bool TryRelease();

    /// <summary>
    /// Raised when a fetch that had delayed this worker's release has settled, so the release
    /// can be attempted again.
    /// </summary>
    event Action? OnReleaseDue;

    /// <summary>
    /// Cancels the fetch this worker has in flight, if any, and completes once it has unwound.
    /// </summary>
    /// <remarks>
    /// Returns as soon as there is nothing in flight. Awaiting the unwind is what lets a
    /// caller know that no result from the cancelled fetch can still land on the query.
    /// </remarks>
    Task CancelCurrentFetchAsync();
}
