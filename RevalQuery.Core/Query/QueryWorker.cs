using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Query;

/// <summary>
/// Orchestrates query execution with polling, invalidation handling, and lifecycle management.
/// </summary>
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