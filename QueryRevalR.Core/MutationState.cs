namespace QueryRevalR;

public sealed class MutationState<TParams, TResponse>(
    Func<MutationHandlerExecutionContext<TParams>, Task<QueryResult<TResponse>>> handler,
    IServiceProvider serviceProvider
)
{
    private QueryResult<TResponse>? _result;
    private int _runningMutationsQuantity = 0;

    public event Action? OnChanged;

    public TResponse? Data => _result is QueryResult<TResponse>.Success s ? s.Value : default;
    public QueryError? Error => _result is QueryResult<TResponse>.Failure f ? f.Error : null;

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

            _result = await handler(ctx);
        }
        catch (Exception ex)
        {
            _result = new QueryResult<TResponse>.Failure(new QueryError("Mutation.Exception", ex.Message));
        }
        finally
        {
            _runningMutationsQuantity--;
            NotifyChanged();
        }

        return _result;
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    public void Reset()
    {
        _result = null;
        NotifyChanged();
    }
}