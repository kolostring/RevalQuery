using System.Runtime.CompilerServices;

namespace QueryRevalR;

public sealed class QueryObserver<TKey, TRes> : IDisposable where TKey : ITuple
{
    public QueryState<TKey, TRes> Query { get; }

    public Func<TRes, Task>? OnSuccess { get; set; }
    public Func<QueryError, Task>? OnError { get; set; }
    public Func<QueryResult<TRes>, Task>? OnSettled { get; set; }

    private readonly FetchOptions _options;
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

    private readonly CancellationTokenSource _linkedCts;
    private readonly Action _onStateChanged;
    private bool _isDisposed;

    public QueryObserver(
        QueryState<TKey, TRes> query,
        Action onStateChanged,
        bool enabled,
        CancellationTokenSource cts,
        FetchOptions options
    )
    {
        Query = query;
        _onStateChanged = onStateChanged;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _enabled = enabled;
        _options = options with
        {
            RetryDelay = options.RetryDelay ?? (attemptIndex => TimeSpan.FromMilliseconds(
                Math.Min(1000 * Math.Pow(2, attemptIndex), 30000)
            ))
        };

        Query.OnChanged += HandleChange;
        Query.OnInvalidated += HandleInvalidation;
        Query.IncrementObservers();
        StartPolling(options.RefetchInterval);
    }

    private void StartPolling(TimeSpan? interval)
    {
        if (interval == null || interval <= TimeSpan.Zero || _isDisposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_isDisposed && !_linkedCts.IsCancellationRequested)
            {
                await Task.Delay(interval.Value, _linkedCts.Token);
                _ = Run();
            }
        }, _linkedCts.Token);
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
        if (elapsedTimeSinceUpdate > (_options.StaleTime ?? TimeSpan.Zero))
        {
            _ = Run();
        }
    }

    private async Task Run()
    {
        if (Query.CanFetch && Enabled)
        {
            await RunQueryWithRetry();
            bool isCancelled = _linkedCts.IsCancellationRequested;

            if (!isCancelled && Query.IsSuccess && OnSuccess is not null)
            {
                await OnSuccess.Invoke(Query.Data!);
                HandleChange();
            }
            else if (!isCancelled && Query.IsError && OnError is not null)
            {
                await OnError.Invoke(Query.Error!);
                HandleChange();
            }

            if (!isCancelled && OnSettled is not null)
            {
                await OnSettled(Query.Res!);
                HandleChange();
            }
        }
    }

    private async Task RunQueryWithRetry()
    {
        for (int attempt = 0; attempt <= _options.Retry; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_options.RetryDelay!(attempt - 1), _linkedCts.Token);
            }

            await Query.Run(_linkedCts.Token);

            if (Query.IsSuccess || _linkedCts.IsCancellationRequested)
                break;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _linkedCts.Cancel();
        _linkedCts.Dispose();

        _isDisposed = true;
        Query.OnChanged -= HandleChange;
        Query.DecrementObservers();
    }
}