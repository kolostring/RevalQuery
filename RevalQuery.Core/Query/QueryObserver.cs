using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Query;

/// <summary>
/// One component's subscription to whichever query its current options name.
/// Created by RevalClient.Subscribe() - manages lifecycle and state notifications.
/// </summary>
/// <remarks>
/// <para>The observer, not the caller, owns which query it watches. Handing it new options
/// with <see cref="SetOptions"/> either re-applies them to the query it is already on or, when
/// the key has changed, moves it to the query the new key names. A caller keeps one observer
/// for the life of a component and never has to notice that the key moved.</para>
/// <para>That is why <see cref="Query"/> can change. Read it again after a render rather than
/// holding on to what it returned earlier.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TRes">The data type returned by the query.</typeparam>
public sealed class QueryObserver<TKey, TRes> : IQueryObserver, IDisposable where TKey : ITuple
{
    private readonly RevalClient _client;
    private readonly Action _onStateHasChanged;

    private readonly object _gate = new();

    private QueryState<TKey, TRes> _query;
    private Action? _handler;
    private bool _isDisposed;

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
    /// <remarks>
    /// Changes when <see cref="SetOptions"/> is given a different key. A single reference
    /// read, so it never blocks behind a switch in progress and never returns a half-moved
    /// answer: it is the old query until the observer holds the new one.
    /// </remarks>
    public IQueryState<TRes> Query => Volatile.Read(ref _query);

    internal QueryObserver(RevalClient client, QueryState<TKey, TRes> query, Action onStateHasChanged, bool enabled)
    {
        _client = client;
        _onStateHasChanged = onStateHasChanged;
        _query = query;
        Enabled = enabled;

        Attach(query);
    }

    /// <summary>
    /// Adopts options rebuilt by a render. The key decides which query the observer watches:
    /// the same key re-applies the options to the query it is on, and a different key moves it.
    /// </summary>
    /// <remarks>
    /// <para>Call it on every render, like <c>QueryObserver.setOptions</c> in TanStack Query.
    /// Against the same key it keeps Enabled, StaleTime, RefetchInterval, RetryOptions and
    /// CacheOptions reactive, and fetches only when that made the query newly enabled.</para>
    /// <para>Against a new key the old query is released exactly as a disposed observer would
    /// release it: it goes on the eviction list, and a fetch it has in flight is left to
    /// finish, because other callers may be awaiting it. The new query is subscribed to like a
    /// fresh <c>Subscribe</c>, so it fetches if its data is stale. Coming back to the old key
    /// inside its GcTime finds it cached.</para>
    /// <para>A result type that does not match the one already registered for the new key
    /// throws before anything has moved, so the observer is left exactly as it was.</para>
    /// </remarks>
    /// <param name="options">The rebuilt query configuration.</param>
    /// <exception cref="InvalidOperationException">The new key is registered with another result type.</exception>
    /// <exception cref="ObjectDisposedException">The observer or the client was disposed.</exception>
    public void SetOptions(QueryOptions<TKey, TRes> options) => _client.SetObserverOptions(this, options);

    /// <summary>
    /// Disposes the observer - unsubscribes and removes state change handler.
    /// Call from component's Dispose method. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Detach(_query, _handler!);
        }
    }

    internal object Gate => _gate;

    internal QueryState<TKey, TRes> Current => _query;

    internal void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    internal (QueryState<TKey, TRes> Query, Action Handler)? Attach(QueryState<TKey, TRes> query)
    {
        var handler = new Action(() =>
        {
            if (ReferenceEquals(Volatile.Read(ref _query), query)) _onStateHasChanged();
        });

        var previous = _handler is null ? ((QueryState<TKey, TRes>, Action)?)null : (_query, _handler);

        _handler = handler;
        Volatile.Write(ref _query, query);

        query.OnChanged += handler;
        query.Subscribe(this);

        return previous;
    }

    internal void Detach(QueryState<TKey, TRes> query, Action handler)
    {
        query.OnChanged -= handler;
        query.Unsubscribe(this);
    }
}
