namespace RevalQuery.Core.Abstractions.Mutation;

/// <summary>
/// Interface for mutation state management.
/// Parallels IQueryState but for mutation (write) operations.
/// </summary>
public interface IMutationState
{
    /// <summary>
    /// Raised when mutation state changes.
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
    /// True when mutation failed (MutationStatus.Exception).
    /// </summary>
    bool IsError { get; }

    /// <summary>
    /// True when mutation completed successfully (MutationStatus.Resolved).
    /// </summary>
    bool IsSuccess { get; }

    /// <summary>
    /// Resets the mutation state to Idle, clearing data and errors.
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
    Exception? Error { get; }
}