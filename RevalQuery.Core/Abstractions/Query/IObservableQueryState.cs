namespace RevalQuery.Core.Abstractions.Query;

public interface IObservableQueryState
{
    event Action? OnChanged;
    void IncrementObservers();
    void DecrementObservers();
}