namespace RevalQuery.TestKit.Tests;

public static class Inline
{
    [ThreadStatic] private static bool _inside;

    public static bool Inside => _inside;

    public static void Run(Action action)
    {
        _inside = true;
        try
        {
            action();
        }
        finally
        {
            _inside = false;
        }
    }
}
