using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RevalQuery.Core;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddRevalQuery();

await builder.Build().RunAsync();
