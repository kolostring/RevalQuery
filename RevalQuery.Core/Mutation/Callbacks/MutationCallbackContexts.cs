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

    /// <summary>The client, for cancelling, setting and invalidating queries.</summary>
    public required RevalClient Client { get; init; }
}
