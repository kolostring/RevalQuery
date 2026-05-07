namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Base interface for observable query state.
/// Provides the observer pattern contract for state change notifications.
/// </summary>
public interface IObservableQueryState
{
    /// <summary>
    /// Raised when the query state changes (data, status, or fetch state).
    /// Components subscribe to trigger UI refresh.
    /// </summary>
    event Action? OnChanged;

    /// <summary>
    /// Subscribes an observer to receive state change notifications.
    /// </summary>
    void Subscribe(IQueryObserver observer);

    /// <summary>
    /// Unsubscribes an observer from state change notifications.
    /// </summary>
    void Unsubscribe(IQueryObserver observer);
}