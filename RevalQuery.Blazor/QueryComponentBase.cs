using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using RevalQuery.Core;

namespace RevalQuery.Blazor;

public abstract class QueryComponentBase : ComponentBase, IDisposable
{
    [Inject] protected QueryClient? Client { get; set; } = null;
    [Inject] protected IServiceProvider ServiceProvider { get; set; }
    [Inject] protected RevalQueryOptions RevalQueryOptions { get; set; }

    private readonly Dictionary<string, IDisposable> _observerSlots = new();
    private bool _isDisposed;

    protected QueryState<TKey, TRes> UseQuery<TKey, TRes>(
        TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<QueryResult<TRes>>> handler,
        Action<QueryOptionsBuilder<TKey, TRes>>? configure = null,
        CancellationTokenSource? cts = null,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        var options = QueryOptionsFactory.Create(key, handler);
        configure?.Invoke(options);
        return UseQuery(options.Build(), cts, line, member);
    }

    private QueryState<TKey, TRes> UseQuery<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        CancellationTokenSource? cts = null,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        var slotId = $"query_{member}_{line}";

        if (_observerSlots.TryGetValue(slotId, out var existing))
        {
            var obs = (QueryObserver<TKey, TRes>)existing;
            obs.OnSuccess = queryOptions.OnSuccess;
            obs.OnError = queryOptions.OnError;
            obs.OnSettled = queryOptions.OnSettled;

            if (obs.Query.Key.Equals(queryOptions.Key))
            {
                obs.Enabled = queryOptions.Enabled;
                return obs.Query;
            }

            obs.Dispose();
        }

        RevalQueryOptions.QueryPluginsPipeline.HandleQueryOptions(queryOptions);

        var prerenderState = new QueryState<TKey, TRes>(
            queryOptions.Key,
            queryOptions.Handler,
            new CacheOptions(TimeSpan.Zero),
            ServiceProvider);

        var state = Client?.GetOrCreateQuery(
                queryOptions.Key,
                queryOptions.Handler,
                queryOptions.CacheOptions ?? new CacheOptions(TimeSpan.Zero)
            ) ?? prerenderState
            ;

        var observer = new QueryObserver<TKey, TRes>(
            RevalQueryOptions,
            state,
            onStateChanged: () => { InvokeAsync(StateHasChanged); },
            queryOptions.Enabled,
            cts,
            queryOptions.FetchOptions
        );

        observer.OnSuccess = queryOptions.OnSuccess;
        observer.OnError = queryOptions.OnError;
        observer.OnSettled = queryOptions.OnSettled;

        _observerSlots[slotId] = observer;
        observer.RunIfStale();

        return state;
    }

    protected MutationState<TParams, TRes> UseMutation<TParams, TRes>(
        Func<MutationHandlerExecutionContext<TParams>, Task<QueryResult<TRes>>> handler,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "")
    {
        string slotId = $"mutation_{member}_{line}";

        if (_observerSlots.TryGetValue(slotId, out var existing))
        {
            var obs = (MutationObserver<TParams, TRes>)existing;
            return obs.State;
        }

        var state = new MutationState<TParams, TRes>(handler, ServiceProvider);

        var observer = new MutationObserver<TParams, TRes>(
            state,
            onStateChanged: () => { InvokeAsync(StateHasChanged); }
        );

        _observerSlots[slotId] = observer;
        return state;
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (var obs in _observerSlots.Values)
        {
            obs.Dispose();
        }

        _observerSlots.Clear();
    }
}