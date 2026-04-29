using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Mutation;

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

    private QueryResult<TResponse>? _result;
    private int _runningMutationsQuantity = 0;

    public event Action? OnChanged;

    public TResponse? Data => _result is QueryResult<TResponse>.Success s ? s.Value : default;
    public Exception? Error => _result is QueryResult<TResponse>.Failure f ? f.Exception : null;

    public bool IsIdle => _runningMutationsQuantity == 0;
    public bool IsFetching => _runningMutationsQuantity > 0;
    public bool IsError => IsIdle && _result is QueryResult<TResponse>.Failure;
    public bool IsSuccess => IsIdle && _result is QueryResult<TResponse>.Success;

    public async Task<QueryResult<TResponse>> ExecuteAsync(TParams variables, CancellationToken ct = default)
    {
        _runningMutationsQuantity++;
        NotifyChanged();

        try
        {
            var ctx = new MutationHandlerExecutionContext<TParams>
            {
                Params = variables,
                ServiceProvider = serviceProvider,
                CancellationToken = ct
            };

            _result = await _retryPolicy.ExecuteWithRetryAsync(
                () => handler(ctx),
                _retryOpts,
                ct
            );
        }
        catch (OperationCanceledException)
        {
            _result = QueryResult.Success<TResponse>(default!);
        }
        catch (Exception ex)
        {
            _result = ex;
        }
        finally
        {
            _runningMutationsQuantity--;
            NotifyChanged();
        }

        return _result;
    }

    private void NotifyChanged()
    {
        OnChanged?.Invoke();
    }

    public void Reset()
    {
        _result = null;
        NotifyChanged();
    }
}