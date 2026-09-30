namespace RevalQuery.Core.Configuration.Options;

/// <summary>
/// Internal immutable retry options with applied defaults.
/// </summary>
public sealed record CoreRetryOptions
{
    /// <summary>
    /// Creates the effective retry options a policy reads.
    /// </summary>
    /// <param name="Retry">
    /// How many further attempts a failure gets. Never counts the first attempt, so zero
    /// still calls the handler once.
    /// </param>
    /// <param name="RetryDelay">Maps a retry's one-based number to the wait before it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="Retry"/> is negative.</exception>
    public CoreRetryOptions(int Retry, Func<int, TimeSpan> RetryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Retry);

        this.Retry = Retry;
        this.RetryDelay = RetryDelay;
    }

    /// <summary>
    /// How many further attempts a failure gets. Never counts the first attempt.
    /// </summary>
    public int Retry { get; }

    /// <summary>
    /// Maps a retry's one-based number to the wait before it.
    /// </summary>
    public Func<int, TimeSpan> RetryDelay { get; }

    private static Func<int, TimeSpan> DefaultDelayCalculator => attempt
        => TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt), 30000));

    /// <summary>
    /// Default for queries: 3 retries after the first attempt, four calls in all, with
    /// exponential backoff.
    /// </summary>
    public static CoreRetryOptions QueryDefault => new(3, DefaultDelayCalculator);

    /// <summary>
    /// Default for mutations: no retries, so one call (fail-fast).
    /// </summary>
    public static CoreRetryOptions MutationDefault => new(0, DefaultDelayCalculator);

    /// <summary>
    /// Applies user overrides to these defaults.
    /// </summary>
    public CoreRetryOptions Apply(RetryOptions? options)
    {
        return options is null
            ? this
            : new CoreRetryOptions(
                options.Retry ?? Retry,
                options.RetryDelay ?? RetryDelay
            );
    }
}

/// <summary>
/// User-facing retry options - nullable fields for overrides.
/// </summary>
public sealed record RetryOptions
{
    /// <summary>
    /// Creates retry options, rejecting a count that leaves no attempt to make.
    /// </summary>
    /// <param name="Retry">
    /// How many further attempts a failure gets, or null to keep the default. Zero is valid
    /// and means one call with no retry.
    /// </param>
    /// <param name="RetryDelay">Maps a retry's one-based number to the wait before it.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="Retry"/> is negative. A negative count is a caller bug, not a
    /// preference: the retry policy would make no attempt at all and the query would fail
    /// without its handler ever running.
    /// </exception>
    public RetryOptions(int? Retry = null, Func<int, TimeSpan>? RetryDelay = null)
    {
        this.Retry = Retry;
        this.RetryDelay = RetryDelay;
    }

    /// <summary>
    /// How many further attempts a failure gets, or null to keep the default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? Retry
    {
        get => _retry;
        // Validated here as well as in the constructor, because an object initialiser and a
        // with-expression both reach this and neither runs the constructor body.
        init => _retry = value < 0
            ? throw new ArgumentOutOfRangeException(
                nameof(Retry), value,
                "A retry count cannot be negative. Use 0 for a single attempt with no retry.")
            : value;
    }

    private readonly int? _retry;

    /// <summary>
    /// Maps a retry's one-based number to the wait before it, or null to keep the default.
    /// </summary>
    public Func<int, TimeSpan>? RetryDelay { get; init; }

    /// <summary>
    /// Creates a new RetryOptionsBuilder.
    /// </summary>
    public static RetryOptionsBuilder Create()
    {
        return new RetryOptionsBuilder();
    }
}

/// <summary>
/// Fluent builder for RetryOptions.
/// </summary>
public sealed class RetryOptionsBuilder
{
    private int? _retry;
    private Func<int, TimeSpan>? _retryDelay;

    /// <summary>
    /// Creates a builder, optionally starting from options already set.
    /// </summary>
    /// <param name="existing">Options to start from, or null to start empty.</param>
    public RetryOptionsBuilder(RetryOptions? existing = null)
    {
        if (existing == null) return;
        _retry = existing.Retry;
        _retryDelay = existing.RetryDelay;
    }

    /// <summary>
    /// Sets the retry count. Optionally provides custom delay calculator.
    /// </summary>
    /// <param name="count">
    /// How many further attempts a failure gets. Never counts the first attempt, so 0 still
    /// calls the handler once and 3 calls it up to four times.
    /// </param>
    /// <param name="delay">Optional custom delay function (attempt -> TimeSpan).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public RetryOptionsBuilder Retry(int count, Func<int, TimeSpan>? delay = null)
    {
        // Checked here as well as in RetryOptions, so a bad literal is reported at the call
        // site that wrote it rather than at whatever line happens to call Build.
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        _retry = count;
        if (delay != null) _retryDelay = delay;
        return this;
    }

    /// <summary>
    /// Builds the RetryOptions.
    /// </summary>
    public RetryOptions Build()
    {
        return new RetryOptions(
            _retry,
            _retryDelay
        );
    }

    /// <summary>
    /// Implicit conversion to RetryOptions.
    /// </summary>
    public static implicit operator RetryOptions(RetryOptionsBuilder builder)
    {
        return builder.Build();
    }
}