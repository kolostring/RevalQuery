using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Scope;

namespace RevalQuery.Tests;

/// <summary>
/// Covers the dependent-query pattern: a component renders once with Enabled(false), then
/// again with Enabled(true) once the value its key depends on arrives. The second render must
/// enable the query and fetch, which means a scope has to apply options rebuilt by a render
/// rather than discarding them when the key is unchanged.
/// </summary>
public class EnabledToggleTests
{
    private static int _calls;

    private static Task<string> CountingHandler(QueryHandlerExecutionContext<ValueTuple<string>> ctx)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult("data");
    }

    // One scope per probe, the way each component has its own. Run is a single call site, so
    // every call to it is the same slot, exactly as repeated renders of one component would be.
    private sealed class Probe(QueryClient client) : IDisposable
    {
        private readonly QueryScope _scope = client.CreateScope();

        public IQueryState<string> Run(bool enabled, TimeSpan? staleTime = null)
        {
            var options = QueryOptions.Create(ValueTuple.Create("toggle"), CountingHandler).Enabled(enabled);
            if (staleTime is not null) options.ConfigureFetch(f => f.StaleTime(staleTime.Value));
            return _scope.Query(options);
        }

        public void Dispose() => _scope.Dispose();
    }

    [Fact]
    public async Task Flipping_Enabled_On_Rerender_Enables_The_Query_And_Fetches()
    {
        _calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        using var probe = new Probe(client);

        // First render: disabled. Nothing fetches.
        var first = probe.Run(enabled: false);
        await Task.Delay(100);
        Assert.False(first.IsEnabled);
        Assert.Equal(0, _calls);

        // Second render with enabled: true. Same key, so the same state comes back, but the
        // rebuilt options must land on it.
        var second = probe.Run(enabled: true);
        await Task.Delay(200);

        Assert.Same(first, second);
        Assert.True(second.IsEnabled);
        Assert.Equal(1, _calls);
        Assert.Equal("data", second.Data);
    }

    [Fact]
    public async Task Flipping_Enabled_Back_Off_Stops_The_Query_Fetching_Again()
    {
        _calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        using var probe = new Probe(client);

        probe.Run(enabled: true);
        await Task.Delay(200);
        Assert.Equal(1, _calls);

        var disabled = probe.Run(enabled: false);
        await Task.Delay(50);
        Assert.False(disabled.IsEnabled);

        // Invalidation asks the worker to run; a disabled query must refuse.
        client.Invalidate(ValueTuple.Create("toggle"));
        await Task.Delay(200);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task A_Rerender_Applies_A_Rebuilt_StaleTime()
    {
        _calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        using var client = new QueryClient(sp, new RevalQueryOptions());

        using var probe = new Probe(client);

        // Created disabled with a long stale time, so the first enable does not fetch.
        probe.Run(enabled: false, staleTime: TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(0, _calls);

        // Enabling re-runs the staleness check. The hour-long stale time still applies, but the
        // data has never been fetched, so LastUpdatedAt is MinValue and the query is stale.
        probe.Run(enabled: true, staleTime: TimeSpan.FromHours(1));
        await Task.Delay(200);
        Assert.Equal(1, _calls);

        // Fresh under an hour: a new observer on the same key must not refetch.
        using var other = new Probe(client);
        other.Run(enabled: true, staleTime: TimeSpan.FromHours(1));
        await Task.Delay(200);
        Assert.Equal(1, _calls);

        // Same key, rebuilt with a zero stale time. The next subscriber sees the new value and
        // refetches, which only works if the rebuilt FetchOptions reached the state.
        probe.Run(enabled: true, staleTime: TimeSpan.Zero);
        using var third = new Probe(client);
        third.Run(enabled: true, staleTime: TimeSpan.Zero);
        await Task.Delay(200);
        Assert.Equal(2, _calls);
    }
}
