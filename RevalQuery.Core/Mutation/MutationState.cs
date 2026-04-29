using System.Collections.Concurrent;
using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Mutation.Options;
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
    MutationOptions<TParams, TResponse> options,
    IServiceProvider serviceProvider
) where TParams : class
{
    private readonly IRetryPolicy _retryPolicy = new ExponentialBackoffRetryPolicy();
    private readonly CoreRetryOptions _retryOpts = CoreRetryOptions.MutationDefault.Apply(options.Retry);
    private readonly object _mutationLock = new();

    private readonly Func<MutationHandlerExecutionContext<TParams>, Task<TResponse>> _handler = options.Handler;

    public TResponse? Data { get; private set; }
    public Exception? Exception { get; private set; }
    public MutationStatus Status { get; private set; } = MutationStatus.Idle;
    private List<CancellationTokenSource> _runningMutationsCancellationTokens = new();
    private int _currentVersion = 0;

    public event Action? OnChanged;

    public bool IsIdle => Status == MutationStatus.Idle;
    public bool IsFetching => Status == MutationStatus.Fetching;
    public bool IsResolved => Status == MutationStatus.Resolved;
    public bool IsException => Status == MutationStatus.Exception;

    public async Task ExecuteAsync(
        TParams variables,
        CancellationToken ct = default,
        MutateOptions<TParams, TResponse>? mutateOptions = null
    )
    {
        using var internalCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, internalCts.Token);
        int version;

        lock (_mutationLock)
        {
            version = ++_currentVersion;
            _runningMutationsCancellationTokens.Add(internalCts);

            if (Status != MutationStatus.Fetching)
            {
                Status = MutationStatus.Fetching;
                NotifyChanged();
            }
        }

        var isMutationCancelled = false;
        try
        {
            var ctx = new MutationHandlerExecutionContext<TParams>
            {
                Params = variables,
                ServiceProvider = serviceProvider,
                CancellationToken = linkedCts.Token
            };

            var resolved = await _retryPolicy.ExecuteWithRetryAsync(
                () => _handler(ctx),
                _retryOpts,
                linkedCts.Token
            );

            bool isLatestMutation;
            lock (_mutationLock)
            {
                isLatestMutation = version == _currentVersion;

                if (isLatestMutation)
                {
                    Status = MutationStatus.Resolved;
                    Data = resolved;
                    Exception = null;
                }
            }

            if (isLatestMutation)
            {
                await (mutateOptions?.OnResolved?.Invoke(resolved, variables) ?? Task.CompletedTask);
            }
        }
        catch (OperationCanceledException)
        {
            isMutationCancelled = true;
        }
        catch (Exception ex)
        {
            bool isLatestMutation;
            lock (_mutationLock)
            {
                isLatestMutation = version == _currentVersion;
                if (isLatestMutation)
                {
                    Status = MutationStatus.Exception;
                    Exception = ex;
                    Data = default;
                }
            }

            if (isLatestMutation)
            {
                await (mutateOptions?.OnException?.Invoke(ex, variables) ?? Task.CompletedTask);
            }
        }
        finally
        {
            bool shouldFireOnSettled;
            lock (_mutationLock)
            {
                shouldFireOnSettled = version == _currentVersion;
                _runningMutationsCancellationTokens.Remove(internalCts);
            }

            if (!isMutationCancelled)
            {
                await (options.OnSettled?.Invoke(Data, Exception, variables) ?? Task.CompletedTask);

                if (shouldFireOnSettled)
                    await (mutateOptions?.OnSettled?.Invoke(Data, Exception, variables) ?? Task.CompletedTask);
            }

            NotifyChanged();
        }
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    public void Reset()
    {
        lock (_mutationLock)
        {
            _currentVersion++;
            _runningMutationsCancellationTokens.ForEach(ct => ct.Cancel());
            _runningMutationsCancellationTokens.Clear();
            Data = default;
            Exception = null;
            Status = MutationStatus.Idle;
        }
        NotifyChanged();
    }
}