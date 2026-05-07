using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions;
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

    private CoreFetchOptions EnsuredFetchOptions => _revalQueryOptions.FetchOptions.Apply(Query.FetchOptions);
    private CoreRetryOptions EnsuredRetryOptions => _revalQueryOptions.RetryOptions.Apply(Query.RetryOptions);

    private QueryState<TKey, TRes> Query { get; }

    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _currentFetchCts;
    private bool _isDisposed;

    /// <summary>
    /// Creates a QueryWorker for a specific query.
    /// </summary>
    /// <param name="revalQueryOptions">Global options including retry and fetch defaults.</param>
    /// <param name="serviceProvider">Service provider for handler dependencies.</param>
    /// <param name="query">The query state to manage.</param>
    /// <param name="cts">Optional cancellation token source.</param>
    /// <param name="retryPolicy">Optional custom retry policy.</param>
    public QueryWorker(
        RevalQueryOptions revalQueryOptions,
        IServiceProvider serviceProvider,
        QueryState<TKey, TRes> query,
        CancellationTokenSource? cts,
        IRetryPolicy? retryPolicy = null
    )
    {
        _serviceProvider = serviceProvider;
        _revalQueryOptions = revalQueryOptions;

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
        _pollingCts?.Cancel();
    }

    private void CancelCurrentFetch()
    {
        _currentFetchCts?.Cancel();
    }

    private void StartPolling(TKey key)
    {
        var interval = EnsuredFetchOptions.RefetchInterval;
        if (interval <= TimeSpan.Zero) return;

        if (_pollingCts?.IsCancellationRequested ?? true)
        {
            _pollingCts?.Dispose();
            _pollingCts = new CancellationTokenSource();
        }

        try
        {
            _ = Task.Run(async () =>
            {
                while (!_isDisposed && !_pollingCts.IsCancellationRequested)
                {
                    await Task.Delay(interval, _pollingCts.Token);
                    RunIfAllowed();
                }
            }, _pollingCts.Token);
        }
        catch (OperationCanceledException)
        {
            //Cancelled polling
        }
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
    /// </summary>
    /// <returns>Internal - use Query.Data to access result.</returns>
    public async Task RunAsync()
    {
        if (Query.IsFetching) return;

        Query.FetchStatus = FetchStatus.Fetching;
        Query.NotifyChanged();

        _currentFetchCts = new CancellationTokenSource();

        var ctx = new QueryHandlerExecutionContext<TKey>
        {
            Key = Query.Key,
            ServiceProvider = _serviceProvider,
            CancellationToken = _currentFetchCts.Token
        };

        try
        {
            Query.Data = await _retryPolicy.ExecuteWithRetryAsync<TRes>(
                () => Query.Handler(ctx),
                EnsuredRetryOptions,
                _currentFetchCts.Token
            );
            Query.SetFresh();
            Query.Status = QueryStatus.Resolved;
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
            _currentFetchCts.Dispose();
            _currentFetchCts = null;
        }

        Query.FetchStatus = FetchStatus.Idle;
        Query.NotifyChanged();
    }

    /// <summary>
    /// Disposes the worker - cancels polling and current fetch, removes event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _currentFetchCts?.Cancel();
        _currentFetchCts?.Dispose();

        _isDisposed = true;
        Query.OnFirstSubscriberAdded -= StartPolling;
        Query.OnLastSubscriberRemoved -= PausePolling;
        Query.OnInvalidated -= HandleInvalidation;
        Query.OnCancelRequested -= CancelCurrentFetch;
    }
}