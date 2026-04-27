namespace RevalQuery.Core.Abstractions.Query;

public interface IObservableQueryState
{
    event Action? OnChanged;
    void Subscribe(IQueryObserver observer);
    void Unsubscribe(IQueryObserver observer);
}