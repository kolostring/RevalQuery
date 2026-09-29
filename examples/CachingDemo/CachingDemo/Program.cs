using CachingDemo.Client.Pages;
using CachingDemo.Client.Services;
using CachingDemo.Components;
using System.Text.Json;
using CachingDemo.Client.Serialization;
using RevalQuery.Blazor;
using RevalQuery.Core;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddRevalQuery();

// Carries what the prerender fetched across to the interactive client, so the browser renders
// the first result set instead of fetching it a second time.
builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(CachingDemo.Client._Imports).Assembly);

app.Run();
