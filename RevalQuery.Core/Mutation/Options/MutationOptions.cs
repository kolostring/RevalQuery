using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Execution;

namespace RevalQuery.Core.Mutation.Options;

/// <summary>
/// Configuration for a mutation (write operation).
/// </summary>
public sealed record MutationOptions<TParams, TRes>(
    Func<MutationHandlerExecutionContext<TParams>, Task<TRes>> Handler,
    RetryOptions? Retry = null,
    Func<TParams, Task>? OnMutate = null,
    Func<TRes, TParams, Task>? OnResolved = null,
    Func<Exception, TParams, Task>? OnException = null,
    Func<TRes?, Exception?, TParams, Task>? OnSettled = null
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
    private Func<TParams, Task>? _onMutate;
    private Func<TRes, TParams, Task>? _onResolved;
    private Func<Exception, TParams, Task>? _onException;
    private Func<TRes?, Exception?, TParams, Task>? _onSettled;

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
    /// Callback fired before mutation executes.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnMutate(Func<TParams, Task> callback)
    {
        _onMutate = callback;
        return this;
    }

    /// <summary>
    /// Callback fired when mutation succeeds.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnResolved(Func<TRes, TParams, Task> callback)
    {
        _onResolved = callback;
        return this;
    }

    /// <summary>
    /// Callback fired when mutation fails.
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnException(Func<Exception, TParams, Task> callback)
    {
        _onException = callback;
        return this;
    }

    /// <summary>
    /// Callback fired when mutation completes (success or failure).
    /// </summary>
    public MutationOptionsBuilder<TParams, TRes> OnSettled(Func<TRes?, Exception?, TParams, Task> callback)
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