using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Query;

public sealed class QueryObserver<TRes> : IDisposable
{
    public IQueryState<TRes> Query { get; }
    private readonly Action _onStateHasChanged;

    public QueryObserver(IQueryState<TRes> query, Action onStateHasChanged)
    {
        Query = query;
        _onStateHasChanged = onStateHasChanged;

        Query.OnChanged += _onStateHasChanged;
        Query.IncrementObservers();
    }

    public void Dispose()
    {
        Query.OnChanged -= _onStateHasChanged;
        Query.DecrementObservers();
    }
}