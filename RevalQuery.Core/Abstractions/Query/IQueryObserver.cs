namespace RevalQuery.Core.Abstractions.Query;

public interface IQueryObserver
{
    bool Enabled { get; set; }
}