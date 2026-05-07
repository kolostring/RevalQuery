namespace RevalQuery.Core.Mutation;

/// <summary>
/// Represents a component's subscription to a mutation state.
/// </summary>
/// <typeparam name="TParams">The parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public class MutationObserver<TParams, TRes> : IDisposable where TParams : class
{
    /// <summary>
    /// The mutation state this observer is subscribed to.
    /// </summary>
    public MutationState<TParams, TRes> State { get; }

    private readonly Action _onStateHasChanged;

    /// <summary>
    /// Creates a MutationObserver subscription.
    /// </summary>
    /// <param name="state">The mutation state to subscribe to.</param>
    /// <param name="onStateHasChanged">Callback to invoke StateHasChanged.</param>
    public MutationObserver(MutationState<TParams, TRes> state, Action onStateHasChanged)
    {
        State = state;
        _onStateHasChanged = onStateHasChanged;

        State.OnChanged += _onStateHasChanged;
    }

    /// <summary>
    /// Disposes the observer - removes state change handler.
    /// </summary>
    public void Dispose()
    {
        State.OnChanged -= _onStateHasChanged;
    }
}