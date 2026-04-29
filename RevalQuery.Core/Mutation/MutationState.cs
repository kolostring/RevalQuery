using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Mutation;

public enum MutationStatus
{
    Idle,
    Fetching,
    Resolved,
    Exception
}

/// <summary>
/// Represents the state of a mutation including data, status, and lifecycle.
/// Supports concurrent mutations tracking.
/// </summary>
public sealed class MutationState<TParams, TResponse>(
    Func<MutationHandlerExecutionContext<TParams>, Task<TResponse>> handler,
    IServiceProvider serviceProvider,
    RetryOptions? retryOptions = null
)
{
    private readonly IRetryPolicy _retryPolicy = new ExponentialBackoffRetryPolicy();
    private readonly CoreRetryOptions _retryOpts = CoreRetryOptions.MutationDefault.Apply(retryOptions);

    public TResponse? Data { get; private set; }
    public Exception? Exception { get; private set; }
    public MutationStatus Status { get; private set; } = MutationStatus.Idle;
    public int RunningMutationsQuantity { get; private set; }

    public event Action? OnChanged;

    public bool IsIdle => Status == MutationStatus.Idle;
    public bool IsFetching => Status == MutationStatus.Fetching;
    public bool IsResolved => Status == MutationStatus.Resolved;
    public bool IsException => Status == MutationStatus.Exception;

    public async Task ExecuteAsync(TParams variables, CancellationToken ct = default)
    {
        var wasIdle = RunningMutationsQuantity == 0;
        if (wasIdle)
        {
            Data = default;
            Exception = null;
            Status = MutationStatus.Fetching;
        }
        
        RunningMutationsQuantity++;
        NotifyChanged();

        try
        {
            var ctx = new MutationHandlerExecutionContext<TParams>
            {
                Params = variables,
                ServiceProvider = serviceProvider,
                CancellationToken = ct
            };

            Data = await _retryPolicy.ExecuteWithRetryAsync(
                () => handler(ctx),
                _retryOpts,
                ct
            );
        }
        catch (OperationCanceledException)
        {
            // Keep previous status/data if canceled
        }
        catch (Exception ex)
        {
            Exception = ex;
        }
        finally
        {
            var wasOnlyOne = RunningMutationsQuantity == 1;
            RunningMutationsQuantity--;
            
            if (wasOnlyOne)
            {
                Status = Exception is null 
                    ? MutationStatus.Resolved 
                    : MutationStatus.Exception;
            }
            NotifyChanged();
        }
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    public void Reset()
    {
        Data = default;
        Exception = null;
        Status = MutationStatus.Idle;
        NotifyChanged();
    }
}