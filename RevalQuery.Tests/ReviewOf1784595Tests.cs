using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Blazor;
using RevalQuery.Blazor.Prerender;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Core.Caching.Eviction;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query.Execution;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Tests;

/// <summary>
/// A result type deliberately left out of <see cref="WidgetContext"/>, standing in for the
/// query a consumer forgot to add to its serializer context.
/// </summary>
public sealed record Gizmo(int Id);

/// <summary>
/// Defects found reviewing 1784595, each one a case the suite did not reach before.
/// </summary>
public class ReviewOf1784595Tests
{
    private static readonly JsonSerializerOptions Serializer =
        new() { TypeInfoResolver = WidgetContext.Default };

    // A type the consumer's resolver does not cover must cost that one query, not the render.
    [Fact]
    public async Task Persisting_Skips_A_Type_The_Resolver_Does_Not_Cover()
    {
        var state = PersistentStateHarness.CreateEmpty(out var written);

        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope().ServiceProvider;
        scope.GetRequiredService<PrerenderTransfer>();
        var client = scope.GetRequiredService<QueryClient>();

        await client.FetchQueryAsync(
            QueryOptions.Create("covered", static _ => Task.FromResult(new Widget(1, "kept"))).Build());

        // GetTypeInfo throws NotSupportedException here rather than returning null, so the
        // persisting callback failed and took the whole prerender with it.
        await client.FetchQueryAsync(
            QueryOptions.Create("uncovered", static _ => Task.FromResult(new Gizmo(2))).Build());

        await PersistentStateHarness.PersistAsync(state);

        Assert.True(written.ContainsKey(PrerenderTransferStateKey));

        var payload = JsonSerializer.Deserialize<string>(written[PrerenderTransferStateKey])!;
        Assert.Contains("covered", payload);
        Assert.DoesNotContain("uncovered", payload);
    }

    // The load half of the same mistake: silent skip, not an exception swallowed upstream.
    [Fact]
    public async Task Loading_Skips_A_Type_The_Resolver_Does_Not_Cover()
    {
        // Written by a server whose resolver covered Gizmo; read by a client whose does not.
        var serverState = PersistentStateHarness.CreateEmpty(out var written);

        var serverServices = new ServiceCollection();
        serverServices.AddSingleton(serverState);
        serverServices.AddRevalQuery();
        serverServices.AddRevalQueryPrerenderTransfer(
            new JsonSerializerOptions { TypeInfoResolver = GizmoContext.Default });

        using var serverProvider = serverServices.BuildServiceProvider();
        var serverScope = serverProvider.CreateScope().ServiceProvider;
        serverScope.GetRequiredService<PrerenderTransfer>();

        await serverScope.GetRequiredService<QueryClient>().FetchQueryAsync(
            QueryOptions.Create("gizmo", static _ => Task.FromResult(new Gizmo(2))).Build());

        await PersistentStateHarness.PersistAsync(serverState);

        var clientState = PersistentStateHarness.CreateFrom(written);
        var transfer = new PrerenderTransfer(clientState, new ServiceCollection().BuildServiceProvider(), Serializer);

        var loaded = await transfer.LoadAsync<Gizmo>(ValueTuple.Create("gizmo"));

        Assert.Null(loaded);
    }

    // One instance is registered under two service types, so the scope disposes it twice.
    [Fact]
    public void Disposing_The_Prerender_Transfer_Twice_Is_Harmless()
    {
        var state = PersistentStateHarness.CreateEmpty(out _);

        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        // Both resolutions, which is what puts the one instance on the scope's disposal list
        // twice. Disposing the scope then calls Dispose on it twice.
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<PrerenderTransfer>(),
            scope.ServiceProvider.GetRequiredService<IQueryPersistence>());

        scope.Dispose();
    }

    // Dispose stops the loop without waiting, so it cannot dispose the source itself.
    [Fact]
    public async Task Stopping_The_Collector_After_Disposing_It_Is_Harmless()
    {
        var collector = new TtlQueryGarbageCollector(new RevalQueryOptions());

        collector.RegisterForEviction(ValueTuple.Create("gc"), null);

        collector.Dispose();
        collector.Dispose();

        // StopAsync returns straight away, because Dispose already marked the collector
        // stopped. The loop it left running is the one that disposes the source.
        await collector.StopAsync();
        await collector.DisposeAsync();
    }

    // A delay calculator is the caller's code, not the handler, and its failure is not a retry.
    [Fact]
    public async Task A_Negative_Retry_Delay_Does_Not_Replace_The_Handler_Failure()
    {
        var policy = new ExponentialBackoffRetryPolicy();
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteWithRetryAsync<string>(
                () =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("the real failure");
                },
                new CoreRetryOptions(2, _ => TimeSpan.FromSeconds(-1))));

        Assert.Equal("the real failure", thrown.Message);
        Assert.Equal(3, calls);
    }

    private const string PrerenderTransferStateKey = "RevalQuery.PrerenderTransfer";
}

[JsonSerializable(typeof(Gizmo))]
[JsonSerializable(typeof(string))]
internal sealed partial class GizmoContext : JsonSerializerContext;
