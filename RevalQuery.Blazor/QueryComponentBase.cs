using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Blazor;

/// <summary>
/// Base component for Blazor components using RevalQuery.
/// Provides UseQuery() and UseMutation() with automatic lifecycle management.
/// </summary>
public abstract class QueryComponentBase : ComponentBase, IDisposable
{
    /// <summary>
    /// Injected QueryClient - configured via DI.
    /// </summary>
    [Inject][NotNull] protected QueryClient? Client { get; set; }

    /// <summary>
    /// Injected IServiceProvider - used for handler dependencies.
    /// </summary>
    [Inject][NotNull] protected IServiceProvider? ServiceProvider { get; set; }

    private readonly Dictionary<string, IDisposable> _observerSlots = [];
    private bool _isDisposed;

    /// <summary>
    /// Subscribes to a query with fluent builder pattern.
    /// Auto-managed: subscribes on call, disposes on component disposal.
    /// </summary>
    /// <typeparam name="TKey">Key type (ITuple).</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="key">Query key.</param>
    /// <param name="handler">Static async handler to fetch data.</param>
    /// <param name="configure">Optional configuration builder.</param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <returns>IQueryState with Data, Status, IsFetching, etc.</returns>
    protected IQueryState<TRes> UseQuery<TKey, TRes>(
          TKey key,
          Func<QueryHandlerExecutionContext<TKey>, Task<TRes>> handler,
          Action<QueryOptionsBuilder<TKey, TRes>>? configure = null,
          [CallerLineNumber] int line = 0,
          [CallerMemberName] string member = "") where TKey : ITuple
    {
        var options = QueryOptions.Create(key, handler);
        configure?.Invoke(options);
        return UseQuery(options.Build(), line, member);
    }

    /// <summary>
    /// Subscribes to a query using pre-built QueryOptions.
    /// </summary>
    protected IQueryState<TRes> UseQuery<TKey, TRes>(
        QueryOptions<TKey, TRes> queryOptions,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        var slotId = $"query_{member}_{line}";

        if (_observerSlots.TryGetValue(slotId, out var existing))
        {
            var obs = (QueryObserver<TRes>)existing;
            var query = (QueryState<TKey, TRes>)obs.Query;

            if (query.Key.Equals(queryOptions.Key))
            {
                return query;
            }

            obs.Dispose();
        }

        var observer = Client.Subscribe(queryOptions, () => { InvokeAsync(StateHasChanged); });

        _observerSlots[slotId] = observer;

        return observer.Query;
    }

    /// <summary>
    /// Creates a mutation with automatic lifecycle.
    /// Call ExecuteAsync on the returned state to run the mutation.
    /// </summary>
    /// <typeparam name="TParams">Mutation parameters type.</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="options">Mutation options including handler and callbacks.</param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <returns>MutationState - call ExecuteAsync() to trigger.</returns>
    protected MutationState<TParams, TRes> UseMutation<TParams, TRes>(
              MutationOptions<TParams, TRes> options,
              [CallerLineNumber] int line = 0,
              [CallerMemberName] string member = ""
          ) where TParams : class
    {
        var slotId = $"mutation_{member}_{line}";

        if (_observerSlots.TryGetValue(slotId, out var existing))
        {
            var obs = (MutationObserver<TParams, TRes>)existing;
            return obs.State;
        }

        var state = new MutationState<TParams, TRes>(options, ServiceProvider);

        var observer = new MutationObserver<TParams, TRes>(
            state,
            () => { InvokeAsync(StateHasChanged); }
        );

        _observerSlots[slotId] = observer;
        return state;
    }

    /// <summary>
    /// Disposes all query and mutation observers.
    /// Called automatically by the Blazor framework.
    /// </summary>
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
        GC.SuppressFinalize(this);
    }
}