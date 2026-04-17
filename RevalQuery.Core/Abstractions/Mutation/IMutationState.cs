namespace RevalQuery.Core.Abstractions.Mutation;

/// <summary>
/// Interface for mutation state management.
/// Parallels IQueryState but for mutation operations.
/// </summary>
public interface IMutationState
{
    event Action? OnChanged;

    bool IsIdle { get; }
    bool IsFetching { get; }
    bool IsError { get; }
    bool IsSuccess { get; }

    void Reset();
}

/// <summary>
/// Generic mutation state interface with typed data access.
/// </summary>
public interface IMutationState<TResponse> : IMutationState
{
    TResponse? Data { get; }
    Exception? Error { get; }
}