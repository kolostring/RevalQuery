namespace RevalQuery.Core.Abstractions.Mutation;

/// <summary>
/// Interface for mutation state management.
/// Parallels IQueryState but for mutation (write) operations, with the same member names.
/// </summary>
public interface IMutationState
{
    /// <summary>
    /// Raised when mutation status changes.
    /// </summary>
    event Action? OnChanged;

    /// <summary>
    /// True when no mutation is in progress.
    /// </summary>
    bool IsIdle { get; }

    /// <summary>
    /// True when mutation handler is executing.
    /// </summary>
    bool IsFetching { get; }

    /// <summary>
    /// True when mutation completed successfully.
    /// </summary>
    bool IsResolved { get; }

    /// <summary>
    /// True when mutation failed.
    /// </summary>
    bool IsException { get; }

    /// <summary>
    /// Resets the mutation state to Idle.
    /// Cancels any running mutations, clears Data and Exception.
    /// Useful for "reset and retry" scenarios.
    /// </summary>
    void Reset();
}

/// <summary>
/// Generic mutation state interface with typed response data access.
/// </summary>
/// <typeparam name="TResponse">The type of data returned by the mutation.</typeparam>
public interface IMutationState<out TResponse> : IMutationState
{
    /// <summary>
    /// The response data from a successful mutation.
    /// </summary>
    TResponse? Data { get; }

    /// <summary>
    /// The exception if the mutation failed.
    /// </summary>
    Exception? Exception { get; }
}
