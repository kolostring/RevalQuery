using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Core.Tests;

public class InvalidateDuringRestoreTests
{
    private readonly FakeTimeProvider _clock = new();
    private PersistedQuery<string> Fresh => new("from-disk", new QueryFreshness(_clock.GetUtcNow()));


    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task An_Invalidation_During_A_Restore_Survives_When_Nothing_Can_Fetch_Yet()
    {
        var calls = new Counter();
        using var persistence = new GatedPersistence();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, persistence: persistence);

        var options = QueryOptions.Create<string>("unobserved", _ =>
            {
                calls.Increment();
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        client.GetOrCreateQuery(options);
        var state = client.StateOf<string>("unobserved");

        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);

        client.Invalidate("unobserved");

        persistence.ReleaseLoad(Fresh);
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, calls.Value);
        Assert.True(state.IsInvalidated);

        using var observer = client.Subscribe(options, () => { });

        Assert.Equal(1, calls.Value);
        Assert.Equal("from-network", observer.Query.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches()
    {
        var calls = new Counter();
        using var persistence = new GatedPersistence();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions { TimeProvider = _clock }, persistence: persistence);

        var options = QueryOptions.Create<string>("gated", _ =>
            {
                calls.Increment();
                return Task.FromResult("from-network");
            })
            .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(10)))
            .Build();

        using var observer = client.Subscribe(options, () => { });
        var state = client.StateOf<string>("gated");

        await persistence.LoadStarted.WaitAsync(TestContext.Current.CancellationToken);

        client.Invalidate("gated");

        Assert.True(state.IsRestoring);
        Assert.Equal(1, calls.Value);
        Assert.Equal("from-network", state.Data);

        persistence.ReleaseLoad(Fresh);
        await state.RestoreCompleted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("from-network", state.Data);
        Assert.Equal(1, calls.Value);
    }
}
