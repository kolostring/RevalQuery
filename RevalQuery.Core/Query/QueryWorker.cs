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
/// Internal component - created and managed by RevalClient.
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
            _pollingCts = null;
        }

        CancelSafely(cts);
    }

    /// <summary>
    /// Adopts options brought by a render, together with the enabled flag of the observer that
    /// brought them.
    /// </summary>
    /// <remarks>
    /// <para>Fetch, retry and cache options belong to the query, not to the observer, so the
    /// most recent render of any component watching this key wins.</para>
    /// <para>Called on a new subscription as well as on a re-render, so a component never
    /// spends its first render running on options somebody else configured.</para>
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

        if (!Query.IsEnabled)
        {
            StopPolling();
            return;
        }

        if (EnsuredFetchOptions.RefetchInterval != previousInterval) StopPolling();

        StartPolling(Query.Key);

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

        lock (_gate)
        {
            cts = _currentFetchCts;
            inFlight = _inFlight is { Task.IsCompleted: false } ? _inFlight.Task : null;
        }

        CancelSafely(cts);

        if (inFlight is null) return;

        await inFlight.ConfigureAwait(false);
    }

    private static void CancelSafely(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
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
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private void HandleInvalidation()
    {
        if (_isDisposed) return;

        RunIfStaleNow();
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
                if (_currentFetchCts is not { IsCancellationRequested: true }) return running.Task;

                superseded = running.Task;
            }

            completion = new TaskCompletionSource<FetchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            fetchCts = new CancellationTokenSource();

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
        var outcome = FetchOutcome.Settled;
        bool releaseDue;

        try
        {
            if (superseded is not null) await superseded.ConfigureAwait(false);

            outcome = await RunFetchAsync(fetchCts);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentFetchCts, fetchCts)) _currentFetchCts = null;

                completion.TrySetResult(outcome);

                releaseDue = _releaseWhenSettled;
                _releaseWhenSettled = false;
            }

            fetchCts.Dispose();

            if (releaseDue) OnReleaseDue?.Invoke();
        }
    }

    private async Task<FetchOutcome> RunFetchAsync(CancellationTokenSource fetchCts)
    {
        lock (_gate)
        {
            if (_isDisposed) return FetchOutcome.NotRun;
        }

        var fetched = default(TRes);
        var succeeded = false;
        var cancelled = false;

        try
        {
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

            fetchCts.Token.ThrowIfCancellationRequested();

            Query.ApplyFetched(fetched);
            succeeded = true;
        }
        catch (OperationCanceledException) when (fetchCts.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex) when (!fetchCts.IsCancellationRequested)
        {
            Query.ApplyFailed(ex);
        }
        catch (Exception)
        {
            cancelled = true;
        }
        finally
        {
            Query.FetchStatus = FetchStatus.Idle;
            NotifyChangedSafely();
        }

        if (succeeded) await SavePersistedAsync(fetched!);

        return cancelled ? FetchOutcome.Cancelled : FetchOutcome.Settled;
    }

    private void NotifyChangedSafely()
    {
        try
        {
            Query.NotifyChanged();
        }
        catch
        {
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

        CancelSafely(pollingCts);
    }
}
