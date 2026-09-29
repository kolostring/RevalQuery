using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using MudBlazorDemo;
using RevalQuery.Core;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// One registration covers the whole app. The client is scoped, which in a standalone
// WebAssembly host means one per browser tab, so every component shares one cache.
builder.Services.AddRevalQuery();

await builder.Build().RunAsync();
