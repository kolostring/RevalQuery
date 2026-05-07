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
///
/// Inherit from this class to access query and mutation functionality:
/// <code>
/// @inherits QueryComponentBase
/// </code>
/// </summary>
public abstract class QueryComponentBase : ComponentBase, IDisposable
{
    /// <summary>
    /// Injected QueryClient - configured via DI.
    /// Used for manual query operations like invalidation, prefetch, and fetch.
    /// </summary>
    [Inject][NotNull] protected QueryClient? Client { get; set; }

    /// <summary>
    /// Injected IServiceProvider - used for resolving dependencies in handlers.
    /// </summary>
    [Inject][NotNull] protected IServiceProvider? ServiceProvider { get; set; }

    private readonly Dictionary<string, IDisposable> _observerSlots = [];
    private bool _isDisposed;

    /// <summary>
    /// Subscribes to a query with inline key and handler.
    /// Auto-managed: subscribes on call, disposes on component disposal.
    /// Use this for simple queries defined directly in the component.
    /// </summary>
    /// <typeparam name="TKey">Key type (ITuple, e.g., (string, int)).</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="key">Query key tuple.</param>
    /// <param name="handler">Static async handler to fetch data.</param>
    /// <param name="configure">Optional configuration builder for fetch/retry/cache options.</param>
    /// <param name="line">Auto-populated: line number (for slot tracking).</param>
    /// <param name="member">Auto-populated: member name (for slot tracking).</param>
    /// <returns>IQueryState with Data, Status, IsFetching, etc.</returns>
    /// <example>
    /// <code>
    /// IQueryState&lt;User[]&gt; Users => UseQuery(
    ///     key: ("users",),
    ///     handler: async static ctx => await ctx.ServiceProvider.GetRequiredService&lt;IUserService&gt;().GetAll(),
    ///     options => options.ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
    /// );
    /// </code>
    /// </example>
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
    /// Subscribes to a query using QueryOptionsBuilder (from factory pattern).
    /// Auto-managed lifecycle.
    /// </summary>
    /// <typeparam name="TKey">Key type (ITuple).</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="queryOptionsBuilder">QueryOptionsBuilder from factory (e.g., UserQueries.GetUserOptions(id)).</param>
    /// <param name="line">Auto-populated: line number.</param>
    /// <param name="member">Auto-populated: member name.</param>
    /// <returns>IQueryState with Data, Status, IsFetching, etc.</returns>
    /// <example>
    /// <code>
    /// IQueryState&lt;User&gt; User => UseQuery(
    ///     UserQueries.GetUserOptions(userId)
    ///         .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
    /// );
    /// </code>
    /// </example>
    protected IQueryState<TRes> UseQuery<TKey, TRes>(
        QueryOptionsBuilder<TKey, TRes> queryOptionsBuilder,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string member = "") where TKey : ITuple
    {
        return UseQuery(queryOptionsBuilder.Build(), line, member);
    }

    /// <summary>
    /// Subscribes to a query using pre-built QueryOptions.
    /// Auto-managed lifecycle.
    /// </summary>
    /// <typeparam name="TKey">Key type (ITuple).</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="queryOptions">Pre-built QueryOptions.</param>
    /// <param name="line">Auto-populated: line number.</param>
    /// <param name="member">Auto-populated: member name.</param>
    /// <returns>IQueryState with Data, Status, IsFetching, etc.</returns>
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
    /// Creates a mutation using MutationOptionsBuilder.
    /// Call ExecuteAsync on the returned state to run the mutation.
    /// Auto-managed lifecycle.
    /// </summary>
    /// <typeparam name="TParams">Mutation parameters type.</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="optionsBuilder">MutationOptionsBuilder with handler and callbacks.</param>
    /// <param name="line">Auto-populated: line number.</param>
    /// <param name="member">Auto-populated: member name.</param>
    /// <returns>MutationState - call ExecuteAsync() to trigger the mutation.</returns>
    /// <example>
    /// <code>
    /// MutationState&lt;CreateUserRequest, User&gt; CreateUser => UseMutation(
    ///     MutationOptions.Create&lt;CreateUserRequest, User&gt;(
    ///         async static ctx => await ctx.ServiceProvider.GetRequiredService&lt;IUserService&gt;().CreateAsync(ctx.Params)
    ///     )
    ///     .OnResolved(async (user, _) => Console.WriteLine($"Created {user.Name}"))
    /// );
    /// </code>
    /// </example>
    protected MutationState<TParams, TRes> UseMutation<TParams, TRes>(
              MutationOptionsBuilder<TParams, TRes> optionsBuilder,
              [CallerLineNumber] int line = 0,
              [CallerMemberName] string member = ""
          ) where TParams : class
    {
        return UseMutation(optionsBuilder.Build(), line, member);
    }

    /// <summary>
    /// Creates a mutation using pre-built MutationOptions.
    /// Call ExecuteAsync on the returned state to run the mutation.
    /// Auto-managed lifecycle.
    /// </summary>
    /// <typeparam name="TParams">Mutation parameters type.</typeparam>
    /// <typeparam name="TRes">Response type.</typeparam>
    /// <param name="options">Pre-built MutationOptions.</param>
    /// <param name="line">Auto-populated: line number.</param>
    /// <param name="member">Auto-populated: member name.</param>
    /// <returns>MutationState - call ExecuteAsync() to trigger the mutation.</returns>
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
    /// Called automatically by the Blazor framework when component is disposed.
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