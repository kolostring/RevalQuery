using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.TestKit;

/// <summary>
/// A query handler whose calls each wait until the test releases them. Calls are numbered from
/// 0 in the order they start, and every per-call member takes that number, before or after the
/// call has started.
/// </summary>
/// <remarks>
/// A call released before it starts completes synchronously, so the library's synchronous path
/// stays testable. A released call resumes on the thread pool, never inside
/// <see cref="Release"/>.
/// </remarks>
/// <typeparam name="T">The handler's result type.</typeparam>
public sealed class GatedHandler<T>
{
    private readonly bool _honourToken;
    private readonly ConcurrentDictionary<int, Call> _calls = new();
    private readonly object _gate = new();
    private int _started;
    private bool _releasedAll;
    private T _releaseAllValue = default!;

    /// <summary>
    /// Creates a handler.
    /// </summary>
    /// <param name="honourToken">
    /// True, the default, for a call that ends with an <see cref="OperationCanceledException"/>
    /// as soon as its token fires. False for a call that ignores its token and waits for its
    /// release regardless, which is how a test reaches the library's handling of handlers that
    /// ignore cancellation.
    /// </param>
    public GatedHandler(bool honourToken = true) => _honourToken = honourToken;

    /// <summary>
    /// How many calls have started.
    /// </summary>
    public int Calls => Volatile.Read(ref _started);

    /// <summary>
    /// The handler. Pass it where the library takes a query handler, as
    /// <c>QueryOptions.Create(key, handler.Handle)</c>.
    /// </summary>
    /// <typeparam name="TKey">The query key type.</typeparam>
    /// <param name="context">The context the library passes the handler.</param>
    public Task<T> Handle<TKey>(QueryHandlerExecutionContext<TKey> context) where TKey : ITuple =>
        Run(context.CancellationToken ?? CancellationToken.None);

    /// <summary>
    /// The handler, for a mutation. Pass it where the library takes a mutation handler, as
    /// <c>MutationOptions.Create&lt;TParams, T&gt;(handler.Handle)</c>. Mutation and query calls
    /// share one numbering.
    /// </summary>
    /// <typeparam name="TParams">The mutation's parameters type.</typeparam>
    /// <param name="context">The context the library passes the handler.</param>
    public Task<T> Handle<TParams>(MutationHandlerExecutionContext<TParams> context) =>
        Run(context.CancellationToken);

    private async Task<T> Run(CancellationToken token)
    {
        var call = At(Interlocked.Increment(ref _started) - 1);
        call.Token = token;
        lock (_gate)
            if (_releasedAll)
                call.Release.TrySetResult(_releaseAllValue);
        token.Register(call.Cancelled.Open);
        call.Started.Open();
        return _honourToken ? await call.Release.Task.WaitAsync(token) : await call.Release.Task;
    }

    /// <summary>
    /// Completes when call <paramref name="index"/> has started.
    /// </summary>
    /// <param name="index">The call's number, from 0.</param>
    public Task Started(int index) => At(index).Started.Task;

    /// <summary>
    /// Completes when the token that call <paramref name="index"/> received fires.
    /// </summary>
    /// <param name="index">The call's number, from 0.</param>
    public Task Cancelled(int index) => At(index).Cancelled.Task;

    /// <summary>
    /// The token call <paramref name="index"/> received, <see cref="CancellationToken.None"/>
    /// when the library passed none.
    /// </summary>
    /// <param name="index">The call's number, from 0.</param>
    /// <exception cref="InvalidOperationException">The call has not started.</exception>
    public CancellationToken Token(int index)
    {
        var call = At(index);
        if (!call.Started.Task.IsCompleted) throw new InvalidOperationException($"Call {index} has not started.");
        return call.Token;
    }

    /// <summary>
    /// Lets call <paramref name="index"/> return <paramref name="value"/>.
    /// </summary>
    /// <param name="index">The call's number, from 0.</param>
    /// <param name="value">What the call returns.</param>
    /// <exception cref="InvalidOperationException">The call was already released or failed.</exception>
    public void Release(int index, T value)
    {
        if (!At(index).Release.TrySetResult(value))
            throw new InvalidOperationException($"Call {index} was already released or failed.");
    }

    /// <summary>
    /// Makes call <paramref name="index"/> throw <paramref name="exception"/>.
    /// </summary>
    /// <param name="index">The call's number, from 0.</param>
    /// <param name="exception">What the call throws.</param>
    /// <exception cref="InvalidOperationException">The call was already released or failed.</exception>
    public void Fail(int index, Exception exception)
    {
        if (!At(index).Release.TrySetException(exception))
            throw new InvalidOperationException($"Call {index} was already released or failed.");
    }

    /// <summary>
    /// Lets every call not yet released return <paramref name="value"/>, including every call
    /// still to come.
    /// </summary>
    /// <param name="value">What the calls return.</param>
    public void ReleaseAll(T value)
    {
        lock (_gate)
        {
            _releasedAll = true;
            _releaseAllValue = value;
        }

        foreach (var call in _calls.Values) call.Release.TrySetResult(value);
    }

    private Call At(int index) => _calls.GetOrAdd(index, _ => new Call());

    private sealed class Call
    {
        public Gate Started { get; } = new();
        public Gate Cancelled { get; } = new();
        public TaskCompletionSource<T> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; set; }
    }
}
