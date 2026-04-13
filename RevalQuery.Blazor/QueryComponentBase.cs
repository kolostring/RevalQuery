using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using RevalQuery.Core;

namespace RevalQuery.Blazor;

public abstract class QueryComponentBase : ComponentBase, IDisposable
{
    [Inject] protected QueryClient? Client { get; set; } = null;
    [Inject] protected IServiceProvider ServiceProvider { get; set; }

    private readonly Dictionary<string, IDisposable> _observerSlots = new();
    private bool _isDisposed;

    protected IQueryState<TRes> UseQuery<TKey, TRes>(
        TKey key,
        Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler,
        Action<QueryOptionsBuilder<TKey, TRes>>? configure = null,
        CancellationTokenSource? cts = null,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        var options = QueryOptionsFactory.Create(key, handler);
        configure?.Invoke(options);
        return UseQuery(options.Build(), cts, line, member);
    }

    protected IQueryState<TRes> UseQuery<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        CancellationTokenSource? cts = null,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        var slotId = $"query_{member}_{line}";

        if (_observerSlots.TryGetValue(slotId, out var existing))
        {
            var obs = (QueryObserver)existing;
            var query = (QueryState<TKey, TRes>)obs.Query;

            if (query.Key.Equals(queryOptions.Key))
            {
                return query;
            }

            obs.Dispose();
        }

        var observer = Client!.Subscribe(queryOptions, () => { InvokeAsync(StateHasChanged); });

        _observerSlots[slotId] = observer;

        return (IQueryState<TRes>)observer.Query;
    }

    protected MutationState<TParams, TRes> UseMutation<TParams, TRes>(
        Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> handler,
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