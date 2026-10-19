namespace RevalQuery.Core.Mutation.Callbacks;

/// <summary>
/// Context passed to <c>OnMutate</c>, before the handler runs.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
public sealed class MutationContext<TParams>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnResolved</c>, after the handler returned.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed class MutationResolvedContext<TParams, TRes>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler returned.</summary>
    public required TRes Result { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run, <c>null</c> when it returned nothing. A builder that knows the type hands it over typed.</summary>
    public object? OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnException</c>, after the handler failed.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
public sealed class MutationExceptionContext<TParams>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler threw.</summary>
    public required Exception Exception { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run, <c>null</c> when it returned nothing. A builder that knows the type hands it over typed.</summary>
    public object? OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnSettled</c>, whichever way the run ended.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
public sealed class MutationSettledContext<TParams, TRes>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler returned, or <c>default</c> when it failed.</summary>
    public TRes? Result { get; init; }

    /// <summary>What the handler threw, or <c>null</c> when it succeeded.</summary>
    public Exception? Exception { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run, <c>null</c> when it returned nothing. A builder that knows the type hands it over typed.</summary>
    public object? OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnResolved</c> by a builder that knows what <c>OnMutate</c> returned.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
/// <typeparam name="TOnMutateResult">What <c>OnMutate</c> returned.</typeparam>
public sealed class MutationResolvedContext<TParams, TRes, TOnMutateResult>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler returned.</summary>
    public required TRes Result { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run.</summary>
    public required TOnMutateResult OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnException</c> by a builder that knows what <c>OnMutate</c> returned.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TOnMutateResult">What <c>OnMutate</c> returned.</typeparam>
public sealed class MutationExceptionContext<TParams, TOnMutateResult>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler threw.</summary>
    public required Exception Exception { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run, or <c>default</c> when <c>OnMutate</c> threw before returning. Check for <c>default</c> before using it.</summary>
    public required TOnMutateResult? OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}

/// <summary>
/// Context passed to <c>OnSettled</c> by a builder that knows what <c>OnMutate</c> returned.
/// </summary>
/// <typeparam name="TParams">The mutation parameters type.</typeparam>
/// <typeparam name="TRes">The response type.</typeparam>
/// <typeparam name="TOnMutateResult">What <c>OnMutate</c> returned.</typeparam>
public sealed class MutationSettledContext<TParams, TRes, TOnMutateResult>
{
    /// <summary>The parameters the run was started with.</summary>
    public required TParams Params { get; init; }

    /// <summary>What the handler returned, or <c>default</c> when it failed.</summary>
    public TRes? Result { get; init; }

    /// <summary>What the handler threw, or <c>null</c> when it succeeded.</summary>
    public Exception? Exception { get; init; }

    /// <summary>What <c>OnMutate</c> returned for this run, or <c>default</c> when <c>OnMutate</c> threw before returning. Check for <c>default</c> before using it.</summary>
    public required TOnMutateResult? OnMutateResult { get; init; }

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}
