using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Query;

public class QueryObserver : IDisposable
{
    public IObservableQueryState Query { get; }
    private readonly Action _onStateHasChanged;

    public QueryObserver(IObservableQueryState query, Action onStateHasChanged)
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