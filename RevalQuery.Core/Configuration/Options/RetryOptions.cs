namespace RevalQuery.Core.Configuration.Options;

public sealed record CoreRetryOptions(
    int Retry,
    Func<int, TimeSpan> RetryDelay
)
{
    private static Func<int, TimeSpan> DefaultDelayCalculator => attempt
        => TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt), 30000));

    public static CoreRetryOptions QueryDefault => new(3, DefaultDelayCalculator);

    public static CoreRetryOptions MutationDefault => new(1, DefaultDelayCalculator);

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

public sealed record RetryOptions(
    int? Retry = null,
    Func<int, TimeSpan>? RetryDelay = null
)
{
    public static RetryOptionsBuilder Create()
    {
        return new RetryOptionsBuilder();
    }
}

public sealed class RetryOptionsBuilder
{
    private int? _retry;
    private Func<int, TimeSpan>? _retryDelay;

    public RetryOptionsBuilder(RetryOptions? existing = null)
    {
        if (existing == null) return;
        _retry = existing.Retry;
        _retryDelay = existing.RetryDelay;
    }

    public RetryOptionsBuilder Retry(int count, Func<int, TimeSpan>? delay = null)
    {
        _retry = count;
        if (delay != null) _retryDelay = delay;
        return this;
    }

    public RetryOptions Build()
    {
        return new RetryOptions(
            _retry,
            _retryDelay
        );
    }

    public static implicit operator RetryOptions(RetryOptionsBuilder builder)
    {
        return builder.Build();
    }
}