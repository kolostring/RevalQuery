namespace RevalQuery.TestKit.Tests;

public class CounterTests
{
    [Fact(Timeout = HangGuardMilliseconds)]
    public void A_New_Counter_Starts_At_Zero()
    {
        Assert.Equal(0, new Counter().Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Incrementing_Returns_The_New_Value()
    {
        var counter = new Counter();

        Assert.Equal(1, counter.Increment());
        Assert.Equal(2, counter.Increment());
        Assert.Equal(2, counter.Value);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public void Concurrent_Increments_Are_All_Counted()
    {
        var counter = new Counter();

        Parallel.For(0, 10_000, _ => counter.Increment());

        Assert.Equal(10_000, counter.Value);
    }
}
