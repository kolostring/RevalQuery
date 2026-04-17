namespace RevalQuery.Core;

public abstract record QueryResult<T>
{
    public sealed record Success(T Value) : QueryResult<T>;

    public sealed record Failure(Exception Exception) : QueryResult<T>;

    public static implicit operator QueryResult<T>(T value)
    {
        return new Success(value);
    }

    public static implicit operator QueryResult<T>(Exception exception)
    {
        return new Failure(exception);
    }
}

public static class QueryResult
{
    public static QueryResult<T> Success<T>(T value)
    {
        return new QueryResult<T>.Success(value);
    }

    public static QueryResult<T> Failure<T>(Exception exception)
    {
        return new QueryResult<T>.Failure(exception);
    }
}