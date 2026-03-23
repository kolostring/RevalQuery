namespace QueryRevalR.Core;

public abstract record QueryResult<T>
{
  public sealed record Success(T Value) : QueryResult<T>;
  public sealed record Failure(QueryError Error) : QueryResult<T>;
  
  public static implicit operator QueryResult<T>(T value) => new Success(value);
  public static implicit operator QueryResult<T>(QueryError error) => new Failure(error);
}

public static class QueryResult
{
  public static QueryResult<T> Success<T>(T value) => new QueryResult<T>.Success(value);
  public static QueryResult<T> Failure<T>(QueryError error) => new QueryResult<T>.Failure(error);
}

public sealed record QueryError(string Code, string Message)
{
  public static readonly QueryError None = new(string.Empty, string.Empty);
  public static readonly QueryError NullValue = new("Error.NullValue", "Null Value");
}