
namespace QueryRevalR;

public sealed class MutationObserver<TParams, TResponse> : IDisposable
{
    public MutationState<TParams, TResponse> State { get; }
    private readonly Action _onStateChanged;
    private bool _isDisposed;

    public MutationObserver(
        MutationState<TParams, TResponse> state,
        Action onStateChanged)
    {
        State = state;
        _onStateChanged = onStateChanged;

        State.OnChanged += HandleStateChange;
    }

    private void HandleStateChange()
    {
        if (_isDisposed) return;

        _onStateChanged.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        State.OnChanged -= HandleStateChange;
    }
}