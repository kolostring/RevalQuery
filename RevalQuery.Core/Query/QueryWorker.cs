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
/// Orchestrates query execution: fetching, retry logic, polling, invalidation handling.
/// Internal component - created and managed by QueryClient.
/// </summary>
/// <typeparam name="TKey">The query key type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed class QueryWorker<TKey, TRes> : IDisposable where TKey : ITuple
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRetryPolicy _retryPolicy;
    private readonly RevalQueryOptions _revalQueryOptions;
    private readonly IQueryPersistence? _persistence;
    private readonly Task _restore;
    private readonly object _gate = new();

    private CoreFetchOptions EnsuredFetchOptions => _revalQueryOptions.FetchOptions.Apply(Query.FetchOptions);
    private CoreRetryOptions EnsuredRetryOptions => _revalQueryOptions.RetryOptions.Apply(Query.RetryOptions);

    private QueryState<TKey, TRes> Query { get; }

    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _currentFetchCts;
    private TaskCompletionSource<bool>? _inFlight;
    private bool _isDisposed;

    /// <summary>
    /// Creates a QueryWorker for a specific query.
    /// </summary>
    /// <param name="revalQueryOptions">Global options including retry and fetch defaults.</param>
    /// <param name="serviceProvider">Service provider for handler dependencies.</param>
    /// <param name="query">The query state to manage.</param>
    /// <param name="persistence">Optional durable store to save successful fetches to.</param>
    /// <param name="restore">
    /// The query's pending load from persistence, awaited before deciding whether its data is
    /// stale. Completed when there is nothing to load.
    /// </param>
    /// <param name="retryPolicy">Optional custom retry policy.</param>
    public QueryWorker(
        RevalQueryOptions revalQueryOptions,
        IServiceProvider serviceProvider,
        QueryState<TKey, TRes> query,
        IQueryPersistence? persistence = null,
        Task? restore = null,
        IRetryPolicy? retryPolicy = null
    )
    {
        _serviceProvider = serviceProvider;
        _revalQueryOptions = revalQueryOptions;
        _persistence = persistence;
        _restore = restore ?? Task.CompletedTask;

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

    private void CancelCurrentFetch()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _currentFetchCts;
        CancelSafely(cts);
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
        }, cts.Token);
    }

    private void HandleInvalidation()
    {
        if (_isDisposed) return;
        RunIfAllowed();
    }

    /// <summary>
    /// Runs the query if its data is stale (beyond StaleTime), once anything held in
    /// persistence has had its chance to land. Called when a new subscriber is added.
    /// </summary>
    public void RunIfStale()
    {
        if (_restore.IsCompleted)
        {
            RunIfStaleNow();
            return;
        }

        _ = RunIfStaleAfterRestoreAsync();
    }

    private async Task RunIfStaleAfterRestoreAsync()
    {
        try
        {
            await _restore;
        }
        catch
        {
            // A failed restore leaves the query to fetch as if nothing was stored
        }

        RunIfStaleNow();
    }

    private void RunIfStaleNow()
    {
        var staleTime = EnsuredFetchOptions.StaleTime;
        var elapsedTimeSinceUpdate = DateTimeOffset.UtcNow - Query.LastUpdatedAt;
        if (elapsedTimeSinceUpdate > staleTime) RunIfAllowed();
    }

    private void RunIfAllowed()
    {
        if (Query.CanFetch) _ = RunAsync();
    }

    /// <summary>
    /// Executes the query handler with retry logic.
    /// Updates Query.Data, Query.Status on success.
    /// Sets Query.Exception, Query.Status on failure.
    /// Concurrent callers join the fetch already in flight rather than starting a second one.
    /// </summary>
    /// <returns>
    /// True when this call ran or joined a fetch, false when the worker was already disposed
    /// and nothing ran. Use Query.Data to access the result.
    /// </returns>
    public Task<bool> RunAsync()
    {
        TaskCompletionSource<bool> completion;

        lock (_gate)
        {
            if (_isDisposed) return Task.FromResult(false);
            if (_inFlight is { Task.IsCompleted: false }) return _inFlight.Task;

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion;
        }

        _ = RunCoreAsync(completion);
        return completion.Task;
    }

    private async Task RunCoreAsync(TaskCompletionSource<bool> completion)
    {
        // Everything below reports through completion exactly once. An observer callback
        // throwing must not strand it: a completion that never settles would leave every later
        // RunAsync joining a task that never finishes, and the query would never fetch again.
        try
        {
            await RunFetchAsync();
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private async Task RunFetchAsync()
    {
        var fetchCts = new CancellationTokenSource();

        lock (_gate)
        {
            if (_isDisposed)
            {
                fetchCts.Dispose();
                return;
            }

            _currentFetchCts = fetchCts;
        }

        var fetched = default(TRes);
        var succeeded = false;

        try
        {
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
            Query.ApplyFetched(fetched);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // Reset to idle, keep previous result if any
        }
        catch (Exception ex)
        {
            Query.Exception = ex;
            Query.Status = QueryStatus.Exception;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentFetchCts, fetchCts)) _currentFetchCts = null;
            }

            fetchCts.Dispose();

            Query.FetchStatus = FetchStatus.Idle;
            NotifyChangedSafely();
        }

        if (succeeded) await SavePersistedAsync(fetched!);
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
            await _persistence.SaveAsync(Query.Key, new PersistedQuery<TRes>(data, Query.LastUpdatedAt));
        }
        catch
        {
            // A persistence adapter failing does not make the fetch a failure: the data is
            // in the registry and usable. Reporting the failure is the adapter's own job.
        }
    }

    /// <summary>
    /// Disposes the worker - cancels polling and current fetch, removes event handlers.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? pollingCts;
        CancellationTokenSource? fetchCts;

        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            pollingCts = _pollingCts;
            fetchCts = _currentFetchCts;
        }

        Query.OnFirstSubscriberAdded -= StartPolling;
        Query.OnLastSubscriberRemoved -= PausePolling;
        Query.OnInvalidated -= HandleInvalidation;
        Query.OnCancelRequested -= CancelCurrentFetch;

        // Cancelled, not disposed: the polling loop still holds its token, and the in-flight
        // fetch disposes its own source when it settles.
        CancelSafely(pollingCts);
        CancelSafely(fetchCts);
    }
}
