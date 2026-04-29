namespace RevalQuery.Core.Mutation;

public class MutationObserver<TParams, TRes> : IDisposable where TParams : class
{
    public MutationState<TParams, TRes> State { get; }
    private readonly Action _onStateHasChanged;

    public MutationObserver(MutationState<TParams, TRes> state, Action onStateHasChanged)
    {
        State = state;
        _onStateHasChanged = onStateHasChanged;

        State.OnChanged += _onStateHasChanged;
    }

    public void Dispose()
    {
        State.OnChanged -= _onStateHasChanged;
    }
}