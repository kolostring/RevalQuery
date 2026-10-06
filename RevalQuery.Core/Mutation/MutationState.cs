using RevalQuery.Core.Abstractions;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Mutation.Callbacks;
using RevalQuery.Core.Mutation.Execution;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Execution;

namespace RevalQuery.Core.Mutation;

/// <summary>
/// Represents the status of a mutation operation.
/// </summary>
public enum MutationStatus
{
    /// <summary>No mutation has run, or the state was reset.</summary>
    Idle,
    /// <summary>The mutation handler is executing.</summary>
    Fetching,
    /// <summary>The mutation completed successfully.</summary>
    Resolved,
    /// <summary>The mutation failed.</summary>
    Exception
}

/// <summary>
/// Represents the state of a mutation (write operation) including data, status, and lifecycle.
/// Supports concurrent mutations.
/// </summary>
/// <remarks>
/// Created by <see cref="RevalClient.CreateMutation{TParams, TRes}"/>, which supplies the
/// service provider handlers receive.
/// </remarks>
/// <typeparam name="TParams">The parameters type for the mutation.</typeparam>
/// <typeparam name="TResponse">The response type from the mutation.</typeparam>
public sealed class MutationState<TParams, TResponse> where TParams : class
{
    private readonly IRetryPolicy _retryPolicy = new ExponentialBackoffRetryPolicy();
    private readonly object _mutationLock = new();
    private readonly IServiceProvider _serviceProvider;

    private MutationOptions<TParams, TResponse> _options;

    private RunOptions? _latestRun;

    private sealed class RunOptions(MutationOptions<TParams, TResponse> options)
    {
        private MutationOptions<TParams, TResponse> _current = options;

        public MutationOptions<TParams, TResponse> Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }

    internal MutationState(MutationOptions<TParams, TResponse> options, IServiceProvider serviceProvider)
    {
        _options = options;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// The response data from a successful mutation.
    /// </summary>
    public TResponse? Data { get; private set; }

    /// <summary>
    /// The exception if the mutation failed.
    /// </summary>
    public Exception? Exception { get; private set; }

    /// <summary>
    /// The current mutation status.
    /// </summary>
    public MutationStatus Status { get; private set; } = MutationStatus.Idle;

    private readonly List<CancellationTokenSource> _runningMutationsCancellationTokens = [];
    private int _currentVersion = 0;

    /// <summary>
    /// Raised when mutation status changes.
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// True when no mutation is in progress.
    /// </summary>
    public bool IsIdle => Status == MutationStatus.Idle;

    /// <summary>
    /// True when mutation handler is executing.
    /// </summary>
    public bool IsFetching => Status == MutationStatus.Fetching;

    /// <summary>
    /// True when mutation completed successfully.
    /// </summary>
    public bool IsResolved => Status == MutationStatus.Resolved;

    /// <summary>
    /// True when mutation failed.
    /// </summary>
    public bool IsException => Status == MutationStatus.Exception;

    /// <summary>
    /// Executes the mutation with the given parameters.
    /// Supports concurrent mutations.
    /// </summary>
    /// <param name="variables">The parameters for the mutation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mutateOptions">Optional per-call callbacks (OnResolved, OnException, OnSettled).</param>
    public async Task ExecuteAsync(
        TParams variables,
        CancellationToken ct = default,
        MutateOptions<TParams, TResponse>? mutateOptions = null
    )
    {
        using var internalCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, internalCts.Token);
        int version;
        RunOptions run;
        MutationOptions<TParams, TResponse> startOptions;

        lock (_mutationLock)
        {
            version = ++_currentVersion;
            startOptions = _options;
            run = _latestRun = new RunOptions(startOptions);
            _runningMutationsCancellationTokens.Add(internalCts);

            if (Status != MutationStatus.Fetching)
            {
                Status = MutationStatus.Fetching;
                NotifyChanged();
            }
        }

        var isMutationCancelled = false;
        TResponse? settledData = default;
        Exception? settledException = null;

        var retryOpts = CoreRetryOptions.MutationDefault.Apply(startOptions.Retry);

        try
        {
            await (run.Current.OnMutate?.Invoke(variables) ?? Task.CompletedTask);

            var ctx = new MutationHandlerExecutionContext<TParams>
            {
                Params = variables,
                ServiceProvider = _serviceProvider,
                CancellationToken = linkedCts.Token
            };

            var resolved = await _retryPolicy.ExecuteWithRetryAsync(
                () => run.Current.Handler(ctx),
                retryOpts,
                linkedCts.Token
            );
            settledData = resolved;

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

            await (run.Current.OnResolved?.Invoke(resolved, variables) ?? Task.CompletedTask);

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
            settledException = ex;

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

            await (run.Current.OnException?.Invoke(ex, variables) ?? Task.CompletedTask);

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

            try
            {
                if (!isMutationCancelled)
                {
                    await (run.Current.OnSettled?.Invoke(settledData, settledException, variables) ?? Task.CompletedTask);

                    if (shouldFireOnSettled)
                        await (mutateOptions?.OnSettled?.Invoke(settledData, settledException, variables) ?? Task.CompletedTask);
                }
            }
            finally
            {
                lock (_mutationLock)
                {
                    if (ReferenceEquals(_latestRun, run)) _latestRun = null;
                }
            }

            NotifyChanged();
        }
    }

    internal void SetOptions(MutationOptions<TParams, TResponse> options)
    {
        lock (_mutationLock)
        {
            _options = options;
            if (_latestRun is { } latest) latest.Current = options;
        }
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    /// <summary>
    /// Resets the mutation state to Idle.
    /// Cancels any running mutations, clears Data and Exception.
    /// Useful for "reset and retry" scenarios.
    /// </summary>
    public void Reset()
    {
        lock (_mutationLock)
        {
            _currentVersion++;

            _latestRun = null;
            _runningMutationsCancellationTokens.ForEach(ct => ct.Cancel());
            _runningMutationsCancellationTokens.Clear();
            Data = default;
            Exception = null;
            Status = MutationStatus.Idle;
        }
        NotifyChanged();
    }
}