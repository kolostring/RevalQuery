using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
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
    private readonly IQueryRetryPolicy _retryPolicy;
    private readonly RevalQueryOptions _revalQueryOptions;

    private CoreFetchOptions EnsuredFetchOptions => _revalQueryOptions.FetchOptions.Apply(Query.FetchOptions);
    private CoreRetryOptions EnsuredRetryOptions => _revalQueryOptions.RetryOptions.Apply(Query.RetryOptions);

    private QueryState<TKey, TRes> Query { get; }

    private readonly CancellationTokenSource _runnerCts = new();
    private readonly CancellationTokenSource? _queryCts;
    private bool _isDisposed;

    public QueryWorker(
        RevalQueryOptions revalQueryOptions,
        IServiceProvider serviceProvider,
        QueryState<TKey, TRes> query,
        CancellationTokenSource? cts,
        IQueryRetryPolicy? retryPolicy = null
    )
    {
        _serviceProvider = serviceProvider;
        _revalQueryOptions = revalQueryOptions;

        Query = query;
        _queryCts = cts;
        _retryPolicy = retryPolicy ?? new ExponentialBackoffRetryPolicy();

        Query.OnInvalidated += HandleInvalidation;
        Query.IncrementObservers();
        StartPolling();
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

        Query.Status = QueryStatus.Fetching;
        Query.NotifyChanged();

        var ctx = new QueryHandlerExecutionContext<TKey>
        {
            Key = Query.Key,
            ServiceProvider = _serviceProvider,
            CancellationToken = _queryCts?.Token
        };

        try
        {
            Query.Result = await _retryPolicy.ExecuteWithRetryAsync<TKey, TRes>(
                () => Query.Handler(ctx),
                EnsuredRetryOptions,
                _runnerCts.Token
            );
            Query.SetFresh();
        }
        catch (Exception ex)
        {
            if (_runnerCts.IsCancellationRequested)
            {
                Query.SetFresh();
                Query.Result = null;
            }
            else
            {
                Query.Result = ex;
            }
        }

        Query.Status = QueryStatus.Idle;
        Query.NotifyChanged();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _runnerCts.Cancel();
        _runnerCts.Dispose();

        _isDisposed = true;
        Query.OnInvalidated -= HandleInvalidation;
        Query.DecrementObservers();
    }
}