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

namespace RevalQuery.Blazor.Tests;

public sealed record Gizmo(int Id);

public class ReviewOf1784595Tests
{
    private static readonly JsonSerializerOptions Serializer =
        new() { TypeInfoResolver = WidgetContext.Default };

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Persisting_Skips_A_Type_The_Resolver_Does_Not_Cover()
    {
        using var harness = await PersistentStateHarness.CreateEmptyAsync();

        var services = new ServiceCollection();
        services.AddSingleton(harness.State);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<PrerenderTransfer>();
        var client = scope.ServiceProvider.GetRequiredService<RevalClient>();

        await client.QueryAsync(
            QueryOptions.Create("covered", static _ => Task.FromResult(new Widget(1, "kept"))).Build(), TestContext.Current.CancellationToken);

        await client.QueryAsync(
            QueryOptions.Create("uncovered", static _ => Task.FromResult(new Gizmo(2))).Build(), TestContext.Current.CancellationToken);

        var written = await harness.PersistAsync();

        Assert.True(written.ContainsKey(PrerenderTransferStateKey));

        var payload = JsonSerializer.Deserialize<string>(written[PrerenderTransferStateKey])!;
        Assert.Contains("System.String:covered", payload);
        Assert.DoesNotContain("uncovered", payload);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Loading_Skips_A_Type_The_Resolver_Does_Not_Cover()
    {
        using var serverState = await PersistentStateHarness.CreateEmptyAsync();

        var serverServices = new ServiceCollection();
        serverServices.AddSingleton(serverState.State);
        serverServices.AddRevalQuery();
        serverServices.AddRevalQueryPrerenderTransfer(
            new JsonSerializerOptions { TypeInfoResolver = GizmoContext.Default });

        using var serverProvider = serverServices.BuildServiceProvider();
        using var serverScope = serverProvider.CreateScope();
        serverScope.ServiceProvider.GetRequiredService<PrerenderTransfer>();

        await serverScope.ServiceProvider.GetRequiredService<RevalClient>().QueryAsync(
            QueryOptions.Create("gizmo", static _ => Task.FromResult(new Gizmo(2))).Build(), TestContext.Current.CancellationToken);

        var written = await serverState.PersistAsync();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var covering = new PrerenderTransfer(
            clientState.State, emptyProvider, new JsonSerializerOptions { TypeInfoResolver = GizmoContext.Default });
        var blind = new PrerenderTransfer(clientState.State, emptyProvider, Serializer);

        var loadedWithTheResolver = await covering.LoadAsync<Gizmo>(ValueTuple.Create("gizmo"), TestContext.Current.CancellationToken);
        var loadedWithoutIt = await blind.LoadAsync<Gizmo>(ValueTuple.Create("gizmo"), TestContext.Current.CancellationToken);

        Assert.Equal(new Gizmo(2), loadedWithTheResolver?.Data);
        Assert.Null(loadedWithoutIt);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task Disposing_The_Scope_Unsubscribes_The_Transfer_Although_It_Is_Disposed_Twice()
    {
        // The transfer is registered under two service types, so the container disposes it twice.
        // Surviving that is not observable from outside: the framework's own unsubscribe is
        // idempotent. What is observable is that the subscription is gone afterwards.
        using var harness = await PersistentStateHarness.CreateEmptyAsync();

        var services = new ServiceCollection();
        services.AddSingleton(harness.State);
        services.AddRevalQuery();
        services.AddRevalQueryPrerenderTransfer(Serializer);

        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<PrerenderTransfer>(),
            scope.ServiceProvider.GetRequiredService<IQueryPersistence>());

        await scope.ServiceProvider.GetRequiredService<RevalClient>().QueryAsync(
            QueryOptions.Create("covered", static _ => Task.FromResult(new Widget(1, "kept"))).Build(), TestContext.Current.CancellationToken);

        scope.Dispose();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PersistAsync());
        Assert.Contains("Nothing was registered", error.Message);
    }

    private const string PrerenderTransferStateKey = "RevalQuery.PrerenderTransfer";
}

[JsonSerializable(typeof(Gizmo))]
[JsonSerializable(typeof(string))]
internal sealed partial class GizmoContext : JsonSerializerContext;
