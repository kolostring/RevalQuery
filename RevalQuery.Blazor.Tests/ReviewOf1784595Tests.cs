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

    [Fact]
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
            QueryOptions.Create("covered", static _ => Task.FromResult(new Widget(1, "kept"))).Build());

        await client.QueryAsync(
            QueryOptions.Create("uncovered", static _ => Task.FromResult(new Gizmo(2))).Build());

        var written = await harness.PersistAsync();

        Assert.True(written.ContainsKey(PrerenderTransferStateKey));

        var payload = JsonSerializer.Deserialize<string>(written[PrerenderTransferStateKey])!;
        Assert.Contains("covered", payload);
        Assert.DoesNotContain("uncovered", payload);
    }

    [Fact]
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
            QueryOptions.Create("gizmo", static _ => Task.FromResult(new Gizmo(2))).Build());

        var written = await serverState.PersistAsync();

        using var clientState = await PersistentStateHarness.CreateFromAsync(written);
        using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var transfer = new PrerenderTransfer(clientState.State, emptyProvider, Serializer);

        var loaded = await transfer.LoadAsync<Gizmo>(ValueTuple.Create("gizmo"));

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Disposing_The_Prerender_Transfer_Twice_Is_Harmless()
    {
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

        scope.Dispose();
    }

    private const string PrerenderTransferStateKey = "RevalQuery.PrerenderTransfer";
}

[JsonSerializable(typeof(Gizmo))]
[JsonSerializable(typeof(string))]
internal sealed partial class GizmoContext : JsonSerializerContext;
