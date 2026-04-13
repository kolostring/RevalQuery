using System.Runtime.CompilerServices;

namespace RevalQuery.Core;

public sealed class QueryObserver : IDisposable
{
    public IQueryState Query { get; }

    private readonly Action _onStateChanged;
    private bool _isDisposed;

    public QueryObserver(
        IQueryState query,
        Action onStateChanged
    )
    {
        Query = query;
        _onStateChanged = onStateChanged;

        Query.OnChanged += HandleChange;
        Query.IncrementObservers();
    }

    private void HandleChange()
    {
        if (_isDisposed)
        {
            return;
        }

        _onStateChanged.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Query.OnChanged -= HandleChange;
        Query.DecrementObservers();
    }
}