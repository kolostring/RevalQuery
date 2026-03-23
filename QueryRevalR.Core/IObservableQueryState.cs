namespace QueryRevalR.Core;

public interface IObservableQueryState
{
    void NotifyInvalidated();
}