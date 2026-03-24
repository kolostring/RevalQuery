namespace RevalQuery.Core;

public interface IObservableQueryState
{
    void NotifyInvalidated();
}