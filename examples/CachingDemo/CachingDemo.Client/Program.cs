using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Text.Json;
using CachingDemo.Client.Persistence;
using CachingDemo.Client.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Abstractions.Persistence;
using RevalQuery.Blazor;
using RevalQuery.Core;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddRevalQuery();

// Carries what the prerender fetched across to the interactive client, so the browser renders
// the first result set instead of fetching it a second time.
builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });

// A second store, behind the transfer, so a load prefers what this very request fetched and
// falls back to what an earlier run left behind. Only the /loading-and-restoring page's keys
// reach it; see SlowDemoPersistence.
builder.Services.AddScoped<IQueryPersistence, SlowDemoPersistence>();

// Stands in for data an earlier run of the app stored. Fresh enough that the query which
// restores it never fetches, which is what makes IsRestoring the only thing to render on.
SlowDemoPersistence.Seed(
    ("persisted", "profile"),
    "profile restored from storage",
    DateTimeOffset.UtcNow);

await builder.Build().RunAsync();
