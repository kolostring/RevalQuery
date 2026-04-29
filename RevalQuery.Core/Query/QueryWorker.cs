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

    private readonly CancellationTokenSource _runnerCts = new();
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

        Query.OnInvalidated += HandleInvalidation;
        Query.OnCancelRequested += CancelCurrentFetch;
        StartPolling();
    }

    private void CancelCurrentFetch()
    {
        _currentFetchCts?.Cancel();
    }

    private void StartPolling()
    {
        var interval = EnsuredFetchOptions.RefetchInterval;

        if (interval <= TimeSpan.Zero || _isDisposed) return;

        _ = Task.Run(async () =>
        {
            while (!_isDisposed && !_runnerCts.IsCancellationRequested)
            {
                await Task.Delay(interval, _runnerCts.Token);
                _ = Run();
            }
        }, _runnerCts.Token);
    }

    private void HandleInvalidation()
    {
        if (_isDisposed) return;
        _ = Run();
    }

    public void RunIfStale()
    {
        var staleTime = EnsuredFetchOptions.StaleTime;
        var elapsedTimeSinceUpdate = DateTimeOffset.UtcNow - Query.LastUpdatedAt;
        if (elapsedTimeSinceUpdate > staleTime) _ = Run();
    }

    private async Task Run()
    {
        if (!Query.CanFetch || Query.IsFetching) return;

        Query.FetchStatus = FetchStatus.Fetching;
        Query.NotifyChanged();

        _currentFetchCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_runnerCts.Token, _currentFetchCts.Token);

        var ctx = new QueryHandlerExecutionContext<TKey>
        {
            Key = Query.Key,
            ServiceProvider = _serviceProvider,
            CancellationToken = linkedCts.Token
        };

        try
        {
            Query.Data = await _retryPolicy.ExecuteWithRetryAsync<TRes>(
                () => Query.Handler(ctx),
                EnsuredRetryOptions,
                linkedCts.Token
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

        Query.OnCancelRequested -= CancelCurrentFetch;
        _runnerCts.Cancel();
        _runnerCts.Dispose();

        _isDisposed = true;
        Query.OnInvalidated -= HandleInvalidation;
    }
}