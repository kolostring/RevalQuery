using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Execution;

namespace RevalQuery.Core.Mutation.Options;

/// <summary>
/// Configuration for a mutation (write operation).
/// </summary>
public sealed record MutationOptions<TParams, TRes>(
    Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> Handler,
    RetryOptions? Retry = null,
    Func<MutationContext<TParams>, Task<object?>>? OnMutate = null,
    Func<MutationResolvedContext<TParams, TRes>, Task>? OnResolved = null,
    Func<MutationExceptionContext<TParams>, Task>? OnException = null,
    Func<MutationSettledContext<TParams, TRes>, Task>? OnSettled = null
) where TParams : class;

/// <summary>
/// Factory for creating MutationOptions with fluent builder.
/// </summary>
public abstract class MutationOptions
{
    /// <summary>
    /// Creates a MutationOptionsBuilder.
    /// </summary>
    public static MutationOptionsBuilder<TParams, TRes> Create<TParams, TRes>(
        Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> handler
    ) where TParams : class => new(handler);
}

/// <summary>
/// Fluent builder for MutationOptions.
/// </summary>
public sealed class MutationOptionsBuilder<TParams, TRes> where TParams : class
{
    private readonly Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> _handler;
    private RetryOptions? _retry;
    private Func<MutationContext<TParams>, Task<object?>>? _onMutate;
    private Func<MutationResolvedContext<TParams, TRes>, Task>? _onResolved;
    private Func<MutationExceptionContext<TParams>, Task>? _onException;
    private Func<MutationSettledContext<TParams, TRes>, Task>? _onSettled;

    /// <summary>
    /// Creates a builder for a mutation driven by the given handler.
    /// </summary>
    /// <param name="handler">The async handler performing the write.</param>
    public MutationOptionsBuilder(Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Configures retry options.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> ConfigureRetry(Action<RetryOptionsBuilder> configure)
    {
        var builder = new RetryOptionsBuilder(_retry);
        configure(builder);
        _retry = builder.Build();
        return this;
    }

    /// <summary>
    /// Callback fired before mutation executes. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnMutate(Func<MutationContext<TParams>, Task> callback)
    {
        _onMutate = async ctx =>
        {
            await callback(ctx);
            return null;
        };
        return this;
    }

    /// <summary>
    /// Callback fired before mutation executes, returning a value, typically rollback data, that
    /// each run keeps and hands to its later callbacks as <c>OnMutateResult</c>. Returns a builder
    /// that knows the type, so those callbacks read it without a cast. Replaces any callback
    /// already set on this builder, and carries over the other callbacks set so far, which see
    /// the value untyped.
    /// </summary>
    /// <typeparam name="TOnMutateResult">What the callback returns.</typeparam>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> OnMutate<TOnMutateResult>(
        Func<MutationContext<TParams>, Task<TOnMutateResult>> callback) =>
        new(_handler, _retry, async ctx => await callback(ctx), _onResolved, _onException, _onSettled);

    /// <summary>
    /// Callback fired when mutation succeeds. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnResolved(Func<MutationResolvedContext<TParams, TRes>, Task> callback)
    {
        _onResolved = callback;
        return this;
    }

    /// <summary>
    /// Callback fired when mutation fails. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnException(Func<MutationExceptionContext<TParams>, Task> callback)
    {
        _onException = callback;
        return this;
    }

    /// <summary>
    /// Callback fired when mutation completes (success or failure). Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnSettled(Func<MutationSettledContext<TParams, TRes>, Task> callback)
    {
        _onSettled = callback;
        return this;
    }

    /// <summary>
    /// Builds the MutationOptions.
    /// </summary>
    public MutationOptions<TParams, TRes> Build()
    {
        return new MutationOptions<TParams, TRes>(
            Handler: _handler,
            Retry: _retry,
            OnMutate: _onMutate,
            OnResolved: _onResolved,
            OnException: _onException,
            OnSettled: _onSettled
        );
    }

    /// <summary>
    /// Implicit conversion to MutationOptions.
    /// </summary>
    public static implicit operator MutationOptions<TParams, TRes>(MutationOptionsBuilder<TParams, TRes> builder) => builder.Build();
}

/// <summary>
/// Fluent builder for MutationOptions whose <c>OnMutate</c> returns a value the later
/// callbacks receive typed.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
/// <typeparam name="TOnMutateResult">What <c>OnMutate</c> returns.</typeparam>
public sealed class MutationOptionsBuilder<TParams, TRes, TOnMutateResult> where TParams : class
{
    private readonly Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> _handler;
    private RetryOptions? _retry;
    private Func<MutationContext<TParams>, Task<object?>>? _onMutate;
    private Func<MutationResolvedContext<TParams, TRes>, Task>? _onResolved;
    private Func<MutationExceptionContext<TParams>, Task>? _onException;
    private Func<MutationSettledContext<TParams, TRes>, Task>? _onSettled;

    internal MutationOptionsBuilder(
        Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> handler,
        RetryOptions? retry,
        Func<MutationContext<TParams>, Task<object?>>? onMutate,
        Func<MutationResolvedContext<TParams, TRes>, Task>? onResolved,
        Func<MutationExceptionContext<TParams>, Task>? onException,
        Func<MutationSettledContext<TParams, TRes>, Task>? onSettled)
    {
        _handler = handler;
        _retry = retry;
        _onMutate = onMutate;
        _onResolved = onResolved;
        _onException = onException;
        _onSettled = onSettled;
    }

    /// <summary>
    /// Configures retry options.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> ConfigureRetry(Action<RetryOptionsBuilder> configure)
    {
        var builder = new RetryOptionsBuilder(_retry);
        configure(builder);
        _retry = builder.Build();
        return this;
    }

    /// <summary>
    /// Callback fired before mutation executes. Its value is kept for this run and handed to the
    /// later callbacks. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> OnMutate(
        Func<MutationContext<TParams>, Task<TOnMutateResult>> callback)
    {
        _onMutate = async ctx => await callback(ctx);
        return this;
    }

    /// <summary>
    /// Callback fired when mutation succeeds. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> OnResolved(
        Func<MutationResolvedContext<TParams, TRes, TOnMutateResult>, Task> callback)
    {
        _onResolved = ctx => callback(new()
        {
            Params = ctx.Params,
            Result = ctx.Result,
            OnMutateResult = (TOnMutateResult)ctx.OnMutateResult!,
            Client = ctx.Client
        });
        return this;
    }

    /// <summary>
    /// Callback fired when mutation fails. Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> OnException(
        Func<MutationExceptionContext<TParams, TOnMutateResult>, Task> callback)
    {
        _onException = ctx => callback(new()
        {
            Params = ctx.Params,
            Exception = ctx.Exception,
            OnMutateResult = (TOnMutateResult)ctx.OnMutateResult!,
            Client = ctx.Client
        });
        return this;
    }

    /// <summary>
    /// Callback fired when mutation completes (success or failure). Replaces any callback already set on this builder.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes, TOnMutateResult> OnSettled(
        Func<MutationSettledContext<TParams, TRes, TOnMutateResult>, Task> callback)
    {
        _onSettled = ctx => callback(new()
        {
            Params = ctx.Params,
            Result = ctx.Result,
            Exception = ctx.Exception,
            OnMutateResult = (TOnMutateResult)ctx.OnMutateResult!,
            Client = ctx.Client
        });
        return this;
    }

    /// <summary>
    /// Builds the MutationOptions.
    /// </summary>
    public MutationOptions<TParams, TRes> Build() =>
        new(_handler, _retry, _onMutate, _onResolved, _onException, _onSettled);

    /// <summary>
    /// Implicit conversion to MutationOptions.
    /// </summary>
    public static implicit operator MutationOptions<TParams, TRes>(MutationOptionsBuilder<TParams, TRes, TOnMutateResult> builder) => builder.Build();
}
