using System.Runtime.CompilerServices;

namespace RevalQuery.Core;

public sealed class QueryObserver<TKey, TRes> : IDisposable where TKey : ITuple
{
    public QueryState<TKey, TRes> Query { get; }

    public Func<TRes, Task>? OnResolved { get; set; }
    public Func<Exception, Task>? OnException { get; set; }
    public Func<QueryResult<TRes>, Task>? OnSettled { get; set; }

    private readonly CoreFetchOptions _options;
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_enabled)
            {
                RunIfStale();
            }
        }
    }

    private readonly CancellationTokenSource _obsCts = new();
    private readonly CancellationTokenSource? _queryCts = null;
    private readonly Action _onStateChanged;
    private bool _isDisposed;

    public QueryObserver(
        RevalQueryOptions RevalQueryOptions,
        QueryState<TKey, TRes> query,
        Action onStateChanged,
        bool enabled,
        CancellationTokenSource? cts,
        FetchOptions? options
    )
    {
        Query = query;
        _onStateChanged = onStateChanged;
        _queryCts = cts;
        _enabled = enabled;

        var defaultOptions = RevalQueryOptions.FetchOptions;
        if (options == null)
        {
            _options = defaultOptions;
        }
        else
        {
            _options = options.PatchNullFields(defaultOptions);
        }

        Query.OnChanged += HandleChange;
        Query.OnInvalidated += HandleInvalidation;
        Query.IncrementObservers();
        StartPolling(_options.RefetchInterval);
    }

    private void StartPolling(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || _isDisposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_isDisposed && !_obsCts.IsCancellationRequested)
            {
                await Task.Delay(interval, _obsCts.Token);
                _ = Run();
            }
        }, _obsCts.Token);
    }

    private void HandleChange()
    {
        if (_isDisposed)
        {
            return;
        }

        _onStateChanged.Invoke();
    }

    private void HandleInvalidation()
    {
        if (_isDisposed) return;

        _ = Run();
    }

    public void RunIfStale()
    {
        if (!Enabled) return;

        var elapsedTimeSinceUpdate = DateTimeOffset.UtcNow - Query.LastUpdatedAt;
        if (elapsedTimeSinceUpdate > _options.StaleTime)
        {
            _ = Run();
        }
    }

    private async Task Run()
    {
        if (Query.CanFetch && Enabled)
        {
            await RunQueryWithRetry();
            bool isCancelled = _queryCts?.IsCancellationRequested ?? false;

            if (!isCancelled && Query.IsResolved && OnResolved is not null)
            {
                await OnResolved.Invoke(Query.Data!);
            }
            else if (!isCancelled && Query.IsException && OnException is not null)
            {
                await OnException.Invoke(Query.Error!);
            }

            if (!isCancelled && OnSettled is not null)
            {
                await OnSettled.Invoke(Query.Res!);
            }
        }
    }

    private async Task RunQueryWithRetry()
    {
        for (int attempt = 0; attempt <= _options.Retry; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_options.RetryDelay!(attempt - 1), _obsCts.Token);
            }

            await Query.Run(_queryCts?.Token);

            if (Query.IsResolved || _obsCts.IsCancellationRequested || (_queryCts?.IsCancellationRequested ?? false))
                break;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _obsCts.Cancel();
        _obsCts.Dispose();

        _isDisposed = true;
        Query.OnChanged -= HandleChange;
        Query.OnInvalidated -= HandleInvalidation;
        Query.DecrementObservers();
    }
}