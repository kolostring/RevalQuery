namespace RevalQuery.Core.Query;

/// <summary>
/// What the registry needs from the worker driving a query, beyond disposing it.
/// </summary>
/// <remarks>
/// A registry node holds workers of every result type at once, so it cannot name
/// <see cref="QueryWorker{TKey, TRes}"/>. This is the non-generic face of one.
/// </remarks>
internal interface IQueryWorker : IDisposable
{
    /// <summary>
    /// Cancels the fetch this worker has in flight, if any, and completes once it has unwound.
    /// </summary>
    /// <remarks>
    /// Returns as soon as there is nothing in flight. Awaiting the unwind is what lets a
    /// caller know that no result from the cancelled fetch can still land on the query.
    /// </remarks>
    Task CancelCurrentFetchAsync();
}
