namespace RevalQuery.Core.Query;

internal interface IQueryWorker : IDisposable
{
    void CancelCurrentFetch();

    bool TryRelease();

    event Action? OnReleaseDue;

    Task CancelCurrentFetchAsync();
}
