using System.Runtime.CompilerServices;

namespace RevalQuery.Core;

public sealed class QueryWorker<TKey, TRes> : IDisposable where TKey : ITuple
{
    private readonly IServiceProvider _serviceProvider;

    private QueryState<TKey, TRes> Query { get; }

    private readonly CancellationTokenSource _runnerCts = new();
    private readonly CancellationTokenSource? _queryCts = null;
    private bool _isDisposed;

    public QueryWorker(
        IServiceProvider serviceProvider,
        QueryState<TKey, TRes> query,
        CancellationTokenSource? cts
    )
    {
        _serviceProvider = serviceProvider;
        Query = query;
        _queryCts = cts;

        Query.OnInvalidated += HandleInvalidation;
        Query.IncrementObservers();
        StartPolling();
    }

    private void StartPolling()
    {
        var interval = Query.FetchOptions.RefetchInterval;
        if (interval <= TimeSpan.Zero || _isDisposed)
        {
            return;
        }

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
        var staleTime = Query.FetchOptions.StaleTime;
        var elapsedTimeSinceUpdate = DateTimeOffset.UtcNow - Query.LastUpdatedAt;
        if (elapsedTimeSinceUpdate > staleTime)
        {
            _ = Run();
        }
    }

    private async Task Run()
    {
        if (!Query.CanFetch || Query.IsFetching)
        {
            return;
        }

        var options = Query.FetchOptions;

        Query.Status = QueryStatus.Fetching;
        Query.NotifyChanged();

        var ctx = new QueryHandlerExecutionContext<TKey>()
        {
            Key = Query.Key,
            ServiceProvider = _serviceProvider,
            CancellationToken = _queryCts?.Token
        };

        for (int attempt = 0; attempt <= options.Retry; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    await Task.Delay(options.RetryDelay!(attempt - 1), _runnerCts.Token);
                }

                Query.Result = await Query.Handler(ctx);
                Query.SetFresh();
                break;
            }
            catch (Exception ex)
            {
                if (_runnerCts.IsCancellationRequested)
                {
                    Query.SetFresh();
                    Query.Result = null;
                }

                if (_runnerCts.IsCancellationRequested || (_queryCts?.IsCancellationRequested ?? false))
                    break;
                Query.Result = ex;
            }
        }

        Query.Status = QueryStatus.Idle;
        Query.NotifyChanged();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _runnerCts.Cancel();
        _runnerCts.Dispose();

        _isDisposed = true;
        Query.OnInvalidated -= HandleInvalidation;
        Query.DecrementObservers();
    }
}