using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Query;

public sealed class QueryObserver<TRes> : IQueryObserver, IDisposable
{

    public bool Enabled { get; set; }
    public IQueryState<TRes> Query { get; }
    private readonly Action _onStateHasChanged;

    public QueryObserver(IQueryState<TRes> query, Action onStateHasChanged, bool enabled)
    {
        Query = query;
        _onStateHasChanged = onStateHasChanged;
        Query.OnChanged += _onStateHasChanged;
        Enabled = enabled;
        Query.Subscribe(this);
    }

    public void Dispose()
    {
        Query.OnChanged -= _onStateHasChanged;
        Query.Unsubscribe(this);
    }
}