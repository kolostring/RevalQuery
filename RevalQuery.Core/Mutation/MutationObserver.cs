using RevalQuery.Core.Mutation.Options;

namespace RevalQuery.Core.Mutation;

/// <summary>
/// Represents a component's subscription to a mutation state.
/// Created by QueryClient.CreateMutation() - hand it new options on every render with
/// <see cref="SetOptions"/>.
/// </summary>
/// <typeparam name="TParams">The parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed class MutationObserver<TParams, TRes> : IDisposable where TParams : class
{
    /// <summary>
    /// The mutation state this observer is subscribed to.
    /// </summary>
    public MutationState<TParams, TRes> State { get; }

    private readonly Action _onStateHasChanged;

    internal MutationObserver(MutationState<TParams, TRes> state, Action onStateHasChanged)
    {
        State = state;
        _onStateHasChanged = onStateHasChanged;

        State.OnChanged += _onStateHasChanged;
    }

    /// <summary>
    /// Adopts options rebuilt by a render.
    /// </summary>
    /// <remarks>
    /// <para>Mirrors <c>MutationObserver.setOptions</c> in TanStack Query. The options replace
    /// the ones every future run starts from, and the latest run, if it is still pending, reads
    /// them too: its handler on its next attempt, and OnMutate, OnResolved, OnException and
    /// OnSettled when each fires. Retry options were fixed when that run started.</para>
    /// <para>Runs older than the latest keep the options they began with, and per-call
    /// <c>MutateOptions</c> are never touched.</para>
    /// <para>Unlike a query there is no key, so nothing here ever moves to another state.</para>
    /// </remarks>
    /// <param name="options">The rebuilt mutation configuration.</param>
    public void SetOptions(MutationOptions<TParams, TRes> options) => State.SetOptions(options);

    /// <summary>
    /// Disposes the observer - removes state change handler. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        State.OnChanged -= _onStateHasChanged;
    }
}
