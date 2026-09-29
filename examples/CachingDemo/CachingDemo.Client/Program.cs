using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Text.Json;
using CachingDemo.Client.Serialization;
using RevalQuery.Blazor;
using RevalQuery.Core;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddRevalQuery();

// Carries what the prerender fetched across to the interactive client, so the browser renders
// the first result set instead of fetching it a second time.
builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });

await builder.Build().RunAsync();
