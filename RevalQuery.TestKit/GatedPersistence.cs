using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Persistence;

namespace RevalQuery.TestKit;

/// <summary>
/// A persistence store whose loads each wait until the test releases them, and which records
/// every save. Releases go to loads in the order the loads started, and a release made before
/// its load starts is kept for it.
/// </summary>
/// <remarks>
/// The library passes a load no cancellation token, so a load nobody releases would outlive its
/// test. Disposing the store releases every pending load, and every later one, with nothing.
/// </remarks>
public sealed class GatedPersistence : IQueryPersistence, IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> _loads = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Save>> _saves = new();
    private readonly ConcurrentQueue<Save> _recorded = new();
    private readonly object _gate = new();
    private readonly Gate _loadStarted = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _saveGates = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _saveStarts = new();
    private readonly ConcurrentQueue<Exception> _saveFailures = new();
    private volatile bool _holdSaves;
    private int _saveBegun;
    private int _saveReleased;
    private int _started;
    private int _released;
    private int _saved;
    private bool _disposed;

    /// <summary>
    /// One recorded save.
    /// </summary>
    /// <param name="Key">The key saved under.</param>
    /// <param name="Entry">The <see cref="PersistedQuery{TRes}"/> saved.</param>
    public sealed record Save(ITuple Key, object Entry);

    /// <summary>
    /// Completes when the first load has started.
    /// </summary>
    public Task LoadStarted => _loadStarted.Task;

    /// <summary>
    /// How many loads have started.
    /// </summary>
    public int Loads => Volatile.Read(ref _started);

    /// <summary>
    /// Every save, in order.
    /// </summary>
    public IReadOnlyCollection<Save> Saves => _recorded;

    /// <summary>
    /// Makes every save made from now on wait until <see cref="ReleaseSave"/> lets it finish.
    /// A held save is not in <see cref="Saves"/> until then, so the list is what the store holds.
    /// </summary>
    public void HoldSaves() => _holdSaves = true;

    /// <summary>
    /// Lets the next unreleased held save finish. A release made before its save starts is kept.
    /// </summary>
    public void ReleaseSave() => SaveGateAt(Interlocked.Increment(ref _saveReleased) - 1).TrySetResult();

    /// <summary>
    /// Makes the next save that has not yet begun throw <paramref name="failure"/> instead of
    /// being recorded. Failures are used in the order they were queued.
    /// </summary>
    /// <param name="failure">What the save throws.</param>
    public void FailNextSave(Exception failure) => _saveFailures.Enqueue(failure);

    /// <summary>
    /// Completes when save <paramref name="index"/>, counted from 0 in the order saves began, has
    /// begun, whether or not it has finished.
    /// </summary>
    /// <param name="index">The save's number, from 0.</param>
    public Task SaveStarted(int index) => SaveStartAt(index).Task;

    /// <summary>
    /// Completes with save <paramref name="index"/>, counted from 0, once it has been made.
    /// </summary>
    /// <param name="index">The save's number, from 0.</param>
    public Task<Save> Saved(int index) => SaveAt(index).Task;

    /// <summary>
    /// Lets the next unreleased load return <paramref name="entry"/>. Its result type must be
    /// the query's, or the load faults, which the library treats as nothing stored.
    /// </summary>
    /// <typeparam name="TRes">The query's result type.</typeparam>
    /// <param name="entry">What the load returns.</param>
    public void ReleaseLoad<TRes>(PersistedQuery<TRes> entry) => ReleaseNext(entry);

    /// <summary>
    /// Lets the next unreleased load return nothing, as though the store held nothing.
    /// </summary>
    public void ReleaseLoad() => ReleaseNext(null);

    /// <inheritdoc />
    public async ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default)
    {
        var load = LoadAt(Interlocked.Increment(ref _started) - 1);
        lock (_gate)
            if (_disposed)
                load.TrySetResult(null);
        _loadStarted.Open();
        var entry = await load.Task;
        return entry is null ? null : (PersistedQuery<TRes>)entry;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default)
    {
        var begun = Interlocked.Increment(ref _saveBegun) - 1;
        SaveStartAt(begun).TrySetResult();
        if (_holdSaves) await SaveGateAt(begun).Task;
        if (_saveFailures.TryDequeue(out var failure)) throw failure;

        var save = new Save(key, entry);
        _recorded.Enqueue(save);
        SaveAt(Interlocked.Increment(ref _saved) - 1).TrySetResult(save);
    }

    /// <summary>
    /// Releases every pending load, and every later one, with nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        foreach (var load in _loads.Values) load.TrySetResult(null);
        foreach (var gate in _saveGates.Values) gate.TrySetResult();
    }

    private void ReleaseNext(object? entry) => LoadAt(Interlocked.Increment(ref _released) - 1).TrySetResult(entry);

    private TaskCompletionSource<object?> LoadAt(int index) =>
        _loads.GetOrAdd(index, _ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource SaveGateAt(int index) =>
        _saveGates.GetOrAdd(index, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource SaveStartAt(int index) =>
        _saveStarts.GetOrAdd(index, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource<Save> SaveAt(int index) =>
        _saves.GetOrAdd(index, _ => new TaskCompletionSource<Save>(TaskCreationOptions.RunContinuationsAsynchronously));
}
