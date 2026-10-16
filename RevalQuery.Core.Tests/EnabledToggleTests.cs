using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Core.Tests;

/// <remarks>
/// Without persistence or a refetch interval nothing is deferred: enabling, invalidating and
/// re-rendering decide whether to fetch before they return, so every assertion follows the call
/// straight away. Each negative is paired with a positive on the same synchronous path.
/// </remarks>
public class EnabledToggleTests
{
    private static Func<QueryHandlerExecutionContext<ValueTuple<string>>, Task<string>> Counting(Counter calls) =>
        _ =>
        {
            calls.Increment();
            return Task.FromResult("data");
        };

    private sealed class Probe(RevalClient client, Counter calls) : IDisposable
    {
        private readonly RevalHooks _hooks = client.CreateHooks();
        private IDisposable? _handle;

        public IQueryState<string> Run(bool enabled, TimeSpan? staleTime = null)
        {
            var options = QueryOptions.Create(ValueTuple.Create("toggle"), Counting(calls)).Enabled(enabled);
            if (staleTime is not null) options.ConfigureFetch(f => f.StaleTime(staleTime.Value));
            _handle ??= _hooks.Attach(() => { });
            return _hooks.Query(options);
        }

        public void Dispose() => _handle?.Dispose();
    }

    private static RevalClient NewClient() =>
        new(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions());

    [Fact]
    public void Flipping_Enabled_On_Rerender_Enables_The_Query_And_Fetches()
    {
        var calls = new Counter();
        using var client = NewClient();
        using var probe = new Probe(client, calls);

        var first = probe.Run(enabled: false);
        Assert.False(first.IsEnabled);
        Assert.Equal(0, calls.Value);

        var second = probe.Run(enabled: true);
        Assert.Same(first, second);
        Assert.True(second.IsEnabled);
        Assert.Equal(1, calls.Value);
        Assert.Equal("data", second.Data);
    }

    [Fact]
    public void Flipping_Enabled_Back_Off_Stops_The_Query_Fetching_Again()
    {
        var calls = new Counter();
        using var client = NewClient();
        using var probe = new Probe(client, calls);

        probe.Run(enabled: true);
        Assert.Equal(1, calls.Value);

        var disabled = probe.Run(enabled: false);
        Assert.False(disabled.IsEnabled);

        client.Invalidate(ValueTuple.Create("toggle"));
        Assert.Equal(1, calls.Value);
    }

    [Fact]
    public void A_Rerender_Applies_A_Rebuilt_StaleTime()
    {
        var calls = new Counter();
        using var client = NewClient();
        using var probe = new Probe(client, calls);

        probe.Run(enabled: false, staleTime: TimeSpan.FromHours(1));
        Assert.Equal(0, calls.Value);

        probe.Run(enabled: true, staleTime: TimeSpan.FromHours(1));
        Assert.Equal(1, calls.Value);

        using var other = new Probe(client, calls);
        other.Run(enabled: true, staleTime: TimeSpan.FromHours(1));
        Assert.Equal(1, calls.Value);

        probe.Run(enabled: true, staleTime: TimeSpan.Zero);
        Assert.Equal(1, calls.Value);

        // Relies on the clock having ticked since the fetch: StaleTime zero counts an age of
        // exactly zero as fresh (L1). Under a fake clock this needs an Advance first.
        using var third = new Probe(client, calls);
        third.Run(enabled: true, staleTime: TimeSpan.Zero);
        Assert.Equal(2, calls.Value);
    }
}
