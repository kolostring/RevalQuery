using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

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
    private readonly object _gate = new();

    private CoreFetchOptions EnsuredFetchOptions => _revalQueryOptions.FetchOptions.Apply(Query.FetchOptions);
    private CoreRetryOptions EnsuredRetryOptions => _revalQueryOptions.RetryOptions.Apply(Query.RetryOptions);

    private QueryState<TKey, TRes> Query { get; }

    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _currentFetchCts;
    private Task? _inFlight;
    private bool _isDisposed;

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

    private void PausePolling(QueryState<TKey, TRes> state)
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _pollingCts;
        CancelSafely(cts);
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
    /// Runs the query if data is stale (beyond StaleTime).
    /// Called when a new subscriber is added.
    /// </summary>
    public void RunIfStale()
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
    /// <returns>A task completing when the fetch settles - use Query.Data to access the result.</returns>
    public Task RunAsync()
    {
        TaskCompletionSource completion;

        lock (_gate)
        {
            if (_isDisposed) return Task.CompletedTask;
            if (_inFlight is { IsCompleted: false }) return _inFlight;

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        _ = RunCoreAsync(completion);
        return completion.Task;
    }

    private async Task RunCoreAsync(TaskCompletionSource completion)
    {
        var fetchCts = new CancellationTokenSource();

        lock (_gate)
        {
            if (_isDisposed)
            {
                fetchCts.Dispose();
                completion.TrySetResult();
                return;
            }

            _currentFetchCts = fetchCts;
        }

        Query.FetchStatus = FetchStatus.Fetching;
        Query.NotifyChanged();

        var ctx = new QueryHandlerExecutionContext<TKey>
        {
            Key = Query.Key,
            ServiceProvider = _serviceProvider,
            CancellationToken = fetchCts.Token
        };

        var fetched = default(TRes);
        var succeeded = false;

        try
        {
            fetched = await _retryPolicy.ExecuteWithRetryAsync<TRes>(
                () => Query.Handler(ctx),
                EnsuredRetryOptions,
                fetchCts.Token
            );
            Query.Data = fetched;
            Query.SetFresh();
            Query.Status = QueryStatus.Resolved;
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
            Query.NotifyChanged();
        }

        if (succeeded) await SavePersistedAsync(fetched!);

        completion.TrySetResult();
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
