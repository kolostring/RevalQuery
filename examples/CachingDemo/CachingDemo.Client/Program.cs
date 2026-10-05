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

builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });

builder.Services.AddScoped<IQueryPersistence, SlowDemoPersistence>();

SlowDemoPersistence.Seed(
    ("persisted", "profile"),
    "profile restored from storage",
    DateTimeOffset.UtcNow);

await builder.Build().RunAsync();
