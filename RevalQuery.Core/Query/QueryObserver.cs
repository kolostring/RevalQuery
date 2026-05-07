using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Core.Query;

/// <summary>
/// Represents a component's subscription to a query.
/// Created by QueryClient.Subscribe() - manages lifecycle and state notifications.
/// </summary>
/// <typeparam name="TRes">The data type returned by the query.</typeparam>
public sealed class QueryObserver<TRes> : IQueryObserver, IDisposable
{
    /// <summary>
    /// Gets or sets whether this observer's subscription is enabled.
    /// When disabled, this observer won't trigger fetches, but other
    /// observers can still fetch. Toggle query without losing cached data.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The query state this observer is subscribed to.
    /// Access Data, Status, IsFetching, etc. through this property.
    /// </summary>
    public IQueryState<TRes> Query { get; }

    private readonly Action _onStateHasChanged;

    /// <summary>
    /// Creates a new QueryObserver subscription.
    /// </summary>
    /// <param name="query">The query state to subscribe to.</param>
    /// <param name="onStateHasChanged">Callback to invoke StateHasChanged on the component.</param>
    /// <param name="enabled">Initial enabled state (default: true).</param>
    public QueryObserver(IQueryState<TRes> query, Action onStateHasChanged, bool enabled)
    {
        Query = query;
        _onStateHasChanged = onStateHasChanged;
        Query.OnChanged += _onStateHasChanged;
        Enabled = enabled;
        Query.Subscribe(this);
    }

    /// <summary>
    /// Disposes the observer - unsubscribes and removes state change handler.
    /// Call from component's Dispose method.
    /// </summary>
    public void Dispose()
    {
        Query.OnChanged -= _onStateHasChanged;
        Query.Unsubscribe(this);
    }
}