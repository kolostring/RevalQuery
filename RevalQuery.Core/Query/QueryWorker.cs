using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Query;

/// <summary>
/// What one run attempt did.
/// </summary>
public enum FetchOutcome
{
    /// <summary>
    /// Nothing ran, because the worker was already released. The caller should take a fresh
    /// worker and try again rather than treat the query's data as fetched.
    /// </summary>
    NotRun,

    /// <summary>The handler ran to a conclusion, either producing data or failing.</summary>
    Settled,

    /// <summary>The fetch was cancelled before it produced anything.</summary>
    Cancelled
}

/// <summary>
/// Orchestrates query execution: fetching, retry logic, polling, invalidation handling.
/// Internal component - created and managed by QueryClient.
/// </summary>
/// <typeparam name="TKey">The query key type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed class QueryWorker<TKey, TRes> : IQueryWorker where TKey : ITuple
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRetryPolicy _retryPolicy;
    private readonly RevalQueryOptions _revalQueryOptions;
    private readonly IQueryPersistence? _persistence;
    private readonly object _gate = new();

    private CoreFetchOptions EnsuredFetchOptions => _revalQueryOptions.FetchOptions.Apply(Query.FetchOptions);
    private CoreRetryOptions EnsuredRetryOptions => _revalQueryOptions.RetryOptions.Apply(Query.RetryOptions);

    private QueryState<TKey, TRes> Query { get; }

    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _currentFetchCts;
    private TaskCompletionSource<FetchOutcome>? _inFlight;
    private bool _isDisposed;
    private bool _releaseWhenSettled;

    /// <inheritdoc />
    public event Action? OnReleaseDue;

    /// <summary>
    /// Creates a QueryWorker for a specific query.
    /// </summary>
    /// <param name="revalQueryOptions">Global options including retry and fetch defaults.</param>
    /// <param name="serviceProvider">Service provider for handler dependencies.</param>
    /// <param name="query">The query state to manage.</param>
    /// <param name="persistence">Optional durable store to save successful fetches to.</param>
    /// <param name="retryPolicy">Optional custom retry policy.</param>
    public QueryWorker(
        RevalQueryOptions revalQueryOptions,
        IServiceProvider serviceProvider,
        QueryState<TKey, TRes> query,
        IQueryPersistence? persistence = null,
        IRetryPolicy? retryPolicy = null
    )
    {
        _serviceProvider = serviceProvider;
        _revalQueryOptions = revalQueryOptions;
        _persistence = persistence;

        Query = query;
        _retryPolicy = retryPolicy ?? new ExponentialBackoffRetryPolicy();

        Query.OnFirstSubscriberAdded += StartPolling;
        Query.OnLastSubscriberRemoved += PausePolling;
        Query.OnInvalidated += HandleInvalidation;
        Query.OnCancelRequested += CancelCurrentFetch;

        if (Query.CanFetch)
        {
            StartPolling(Query.Key);
        }
    }

    private void PausePolling(QueryState<TKey, TRes> state) => StopPolling();

    private void StopPolling()
    {
        CancellationTokenSource? cts;

        lock (_gate)
        {
            cts = _pollingCts;
            // Cleared under the lock, so a subscriber arriving next does not see a source that
            // is about to be cancelled and skip starting its own loop.
            _pollingCts = null;
        }

        CancelSafely(cts);
    }

    /// <summary>
    /// Adopts options rebuilt by a re-render, together with the new enabled flag of the observer
    /// that rebuilt them.
    /// </summary>
    /// <remarks>
    /// <para>Fetch, retry and cache options belong to the query, not to the observer, so the
    /// most recent render of any component watching this key wins.</para>
    /// <para>A query that has just become enabled fetches if its data is stale, exactly as a
    /// query gaining its first subscriber does. This is what makes the dependent-query pattern
    /// work: a component renders once with Enabled(false), then again with Enabled(true) once
    /// the value its key depends on arrives.</para>
    /// </remarks>
    /// <param name="options">The rebuilt options.</param>
    /// <param name="observer">The observer whose render produced them.</param>
    public void ApplyOptions(QueryOptions<TKey, TRes> options, IQueryObserver observer)
    {
        var wasEnabled = Query.IsEnabled;
        var previousInterval = EnsuredFetchOptions.RefetchInterval;

        Query.FetchOptions = options.FetchOptions;
        Query.RetryOptions = options.RetryOptions;
        Query.CacheOptions = options.CacheOptions;
        observer.Enabled = options.Enabled;

        // Read after the assignments: another observer may keep the query enabled even when
        // this one just disabled itself.
        if (!Query.IsEnabled)
        {
            StopPolling();
            return;
        }

        // The loop captured the old interval when it started, so a changed one needs a new loop.
        if (!wasEnabled || EnsuredFetchOptions.RefetchInterval != previousInterval)
        {
            StopPolling();
            StartPolling(Query.Key);
        }

        if (!wasEnabled) RunIfStale();
    }

    /// <inheritdoc />
    public bool TryRelease()
    {
        lock (_gate)
        {
            if (_isDisposed) return true;
            if (_inFlight is not { Task.IsCompleted: false }) return true;

            _releaseWhenSettled = true;
            return false;
        }
    }

    /// <inheritdoc />
    public void CancelCurrentFetch()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _currentFetchCts;
        CancelSafely(cts);
    }

    /// <inheritdoc />
    public async Task CancelCurrentFetchAsync()
    {
        CancellationTokenSource? cts;
        Task? inFlight;

        // Both read under the one lock, so the task awaited below is the task the source
        // cancelled belongs to rather than a later fetch that started in between.
        lock (_gate)
        {
            cts = _currentFetchCts;
            inFlight = _inFlight is { Task.IsCompleted: false } ? _inFlight.Task : null;
        }

        CancelSafely(cts);

        if (inFlight is null) return;

        // The run reports its own cancellation through the outcome, so nothing thrown here
        // needs handling: the caller asked for the fetch to stop, not for its result.
        await inFlight.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a source that the fetch it belongs to may have already finished with and disposed.
    /// </summary>
    private static void CancelSafely(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The fetch settled first, so there is nothing left to cancel
        }
    }

    private void StartPolling(TKey key)
    {
        var interval = EnsuredFetchOptions.RefetchInterval;
        if (interval <= TimeSpan.Zero) return;

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_isDisposed) return;
            if (_pollingCts is { IsCancellationRequested: false }) return;

            cts = new CancellationTokenSource();
            _pollingCts = cts;
        }

        // No token passed to Task.Run: an already-cancelled one would skip the delegate, and
        // with it the disposal below. StopPolling tolerates a source this loop disposed first.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(interval, cts.Token);
                    RunIfAllowed();
                }
            }
            catch (OperationCanceledException)
            {
                // Polling was paused or the worker was disposed
            }
            finally
            {
                // The loop is the last user of this source. Every options change and every
                // subscribe cycle makes a new one, so leaving them undisposed accumulates
                // timer registrations for the life of the circuit.
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Refetches on an invalidation, unless the query is static.
    /// </summary>
    /// <remarks>
    /// The static check belongs here, at the refetch decision, rather than at the mark in
    /// QueryClient.Invalidate. Invalidation is one thing and refetching is another: a static
    /// query that was invalidated is still invalidated, and reports so, it simply does not act
    /// on it. Putting the check at the mark would make the two the same thing and leave the
    /// query claiming it was never invalidated at all.
    /// </remarks>
    private void HandleInvalidation()
    {
        if (_isDisposed) return;
        if (EnsuredFetchOptions.Static) return;

        RunIfAllowed();
    }

    /// <summary>
    /// Runs the query if its data is stale (beyond StaleTime). Called when a new subscriber is
    /// added.
    /// </summary>
    /// <remarks>
    /// While a restore is outstanding the decision is handed to it rather than made here and
    /// repeated later. The restore then makes it as its last act, so a fetch that follows a
    /// restore has already started by the time anything reports the restore over.
    /// </remarks>
    public void RunIfStale()
    {
        if (Query.TryDeferUntilRestored(RunIfStaleNow)) return;

        RunIfStaleNow();
    }

    private void RunIfStaleNow()
    {
        // The ordering the decision needs lives on the query, where the timestamp and the
        // invalidation flag can be read as one, and where every caller asking whether data is
        // stale gets the same answer.
        if (Query.IsStale(EnsuredFetchOptions)) RunIfAllowed();
    }

    private void RunIfAllowed()
    {
        if (Query.CanFetch) _ = RunAsync();
    }

    /// <summary>
    /// Executes the query handler with retry logic.
    /// Updates Query.Data, Query.Status on success.
    /// Sets Query.Exception, Query.Status on failure.
    /// Concurrent callers join the fetch already in flight rather than starting a second one,
    /// unless that fetch has already been asked to cancel, in which case they get a fresh one.
    /// </summary>
    /// <returns>
    /// What the run did. Use Query.Data to access the result of a settled one.
    /// </returns>
    public Task<FetchOutcome> RunAsync()
    {
        TaskCompletionSource<FetchOutcome> completion;
        CancellationTokenSource fetchCts;
        Task? superseded = null;

        lock (_gate)
        {
            if (_isDisposed) return Task.FromResult(FetchOutcome.NotRun);

            if (_inFlight is { Task.IsCompleted: false } running)
            {
                // A fetch whose cancellation has already been requested is not the fetch this
                // caller asked for. Joining it would report FetchOutcome.Cancelled for a run
                // requested after the cancel, and the caller would be told its own fresh fetch
                // had been cancelled by something that happened before it started. Wait for
                // that one to unwind instead, then fetch again.
                if (_currentFetchCts is not { IsCancellationRequested: true }) return running.Task;

                superseded = running.Task;
            }

            completion = new TaskCompletionSource<FetchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            fetchCts = new CancellationTokenSource();

            // Published together. A cancellation landing between the two would otherwise read a
            // null source beside a live fetch, cancel nothing, and then wait out the very fetch
            // it was meant to stop while its result was applied.
            _inFlight = completion;
            _currentFetchCts = fetchCts;
        }

        _ = RunCoreAsync(completion, fetchCts, superseded);
        return completion.Task;
    }

    private async Task RunCoreAsync(
        TaskCompletionSource<FetchOutcome> completion,
        CancellationTokenSource fetchCts,
        Task? superseded)
    {
        // Everything below reports through completion exactly once. An observer callback
        // throwing must not strand it: a completion that never settles would leave every later
        // RunAsync joining a task that never finishes, and the query would never fetch again.
        // Settled is the default for that case: the fetch did happen, and something downstream
        // of it threw, so retrying would repeat work rather than recover anything.
        var outcome = FetchOutcome.Settled;
        bool releaseDue;

        try
        {
            // The fetch this one replaces reports its own cancellation through its own outcome,
            // so nothing it produces is needed here. Waiting for it is what keeps the two from
            // overlapping: the one unwinding puts the fetch status back to idle as it goes, and
            // an observer told fetching had stopped while this one was only starting would drop
            // its spinner for a frame.
            if (superseded is not null) await superseded.ConfigureAwait(false);

            outcome = await RunFetchAsync(fetchCts);
        }
        finally
        {
            lock (_gate)
            {
                // Retired together, the way they were published. A source cleared while its
                // own task was still incomplete would let the next caller join a fetch without
                // being able to tell that it had been cancelled.
                if (ReferenceEquals(_currentFetchCts, fetchCts)) _currentFetchCts = null;

                // Inside the lock for the same reason. Continuations were asked to run
                // asynchronously, so nothing joined to this task runs while it is held.
                completion.TrySetResult(outcome);

                releaseDue = _releaseWhenSettled;
                _releaseWhenSettled = false;
            }

            fetchCts.Dispose();

            // Raised off the lock, because the handler takes the registry's. Whether the
            // release still applies is the registry's question, not this worker's: a component
            // may have remounted while the fetch was running, and a fetch that supersedes this
            // one may already be in flight.
            if (releaseDue) OnReleaseDue?.Invoke();
        }
    }

    private async Task<FetchOutcome> RunFetchAsync(CancellationTokenSource fetchCts)
    {
        lock (_gate)
        {
            // Released between RunAsync taking the lock and this line. Reporting that as a run
            // would let QueryAsync return the query's data as though this call had
            // produced it, when nothing was fetched at all.
            if (_isDisposed) return FetchOutcome.NotRun;
        }

        var fetched = default(TRes);
        var succeeded = false;
        var cancelled = false;

        try
        {
            // Cancelled between RunAsync publishing this source and the handler being reached,
            // which is the window a superseded fetch is waited out in. Starting the handler now
            // would ignore a cancellation that had already been asked for.
            fetchCts.Token.ThrowIfCancellationRequested();

            Query.FetchStatus = FetchStatus.Fetching;
            NotifyChangedSafely();

            var ctx = new QueryHandlerExecutionContext<TKey>
            {
                Key = Query.Key,
                ServiceProvider = _serviceProvider,
                CancellationToken = fetchCts.Token
            };

            fetched = await _retryPolicy.ExecuteWithRetryAsync<TRes>(
                () => Query.Handler(ctx),
                EnsuredRetryOptions,
                fetchCts.Token
            );

            // A handler that never read its token still returns a value. Applying it would
            // make Cancel a request the query is free to ignore, and would overwrite whatever
            // replaced this fetch: the optimistic value a mutation just wrote, or a newer
            // fetch's result. The value is dropped instead, the same as the result of a
            // cancelled retryer in TanStack Query.
            fetchCts.Token.ThrowIfCancellationRequested();

            Query.ApplyFetched(fetched);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // Reset to idle, keep previous result if any. Reported rather than swallowed: a
            // caller awaiting this fetch asked for data and is getting none.
            cancelled = true;
        }
        catch (Exception ex) when (!fetchCts.IsCancellationRequested)
        {
            Query.ApplyFailed(ex);
        }
        catch (Exception)
        {
            // Cancellation was requested and the handler reported the abort as its own
            // exception type rather than an OperationCanceledException: an aborted socket
            // surfacing as an IOException, say. The retry policy stops retrying once
            // cancellation is requested and rethrows whatever the handler produced, so this is
            // where such a failure lands. Recording it would contradict CancelAsync, which
            // promises a cancelled query records no error, and would mark the query settled
            // and so block any later restore.
            cancelled = true;
        }
        finally
        {
            // The source outlives this method: RunCoreAsync retires it together with the
            // completion, so that the two never disagree about whether a fetch is live.
            Query.FetchStatus = FetchStatus.Idle;
            NotifyChangedSafely();
        }

        if (succeeded) await SavePersistedAsync(fetched!);

        return cancelled ? FetchOutcome.Cancelled : FetchOutcome.Settled;
    }

    /// <summary>
    /// Notifies observers without letting one of them break the fetch. A Blazor component
    /// whose circuit went away throws from StateHasChanged, and that is not this query's problem.
    /// </summary>
    private void NotifyChangedSafely()
    {
        try
        {
            Query.NotifyChanged();
        }
        catch
        {
            // An observer that cannot render is the observer's problem, not the query's
        }
    }

    private async Task SavePersistedAsync(TRes data)
    {
        if (_persistence is null) return;

        try
        {
            await _persistence.SaveAsync(Query.Key, new PersistedQuery<TRes>(data, Query.Freshness));
        }
        catch
        {
            // A persistence adapter failing does not make the fetch a failure: the data is
            // in the registry and usable. Reporting the failure is the adapter's own job.
        }
    }

    /// <summary>
    /// Stops the worker driving this query: ends polling, detaches from the query's events and
    /// refuses any further run.
    /// </summary>
    /// <remarks>
    /// A fetch already in flight is left to finish. Disposal happens whenever the last
    /// component watching a key unmounts, and a fetch some other caller is awaiting has nothing
    /// to do with that component going away: cancelling it here handed that caller an
    /// OperationCanceledException for a cancellation nobody asked for. Callers that do mean to
    /// stop the fetch use <see cref="CancelCurrentFetch"/> or
    /// <see cref="CancelCurrentFetchAsync"/> first.
    /// </remarks>
    public void Dispose()
    {
        CancellationTokenSource? pollingCts;

        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            pollingCts = _pollingCts;
        }

        Query.OnFirstSubscriberAdded -= StartPolling;
        Query.OnLastSubscriberRemoved -= PausePolling;
        Query.OnInvalidated -= HandleInvalidation;
        Query.OnCancelRequested -= CancelCurrentFetch;

        // Cancelled, not disposed: the polling loop still holds its token.
        CancelSafely(pollingCts);
    }
}
