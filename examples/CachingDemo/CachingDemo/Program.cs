using CachingDemo.Client.Pages;
using CachingDemo.Client.Services;
using CachingDemo.Components;
using System.Text.Json;
using CachingDemo.Client.Serialization;
using RevalQuery.Blazor;
using RevalQuery.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddRevalQuery();

builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
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
