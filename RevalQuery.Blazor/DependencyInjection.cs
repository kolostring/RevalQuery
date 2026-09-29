using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Blazor.Prerender;
using RevalQuery.Core.Abstractions.Persistence;

namespace RevalQuery.Blazor;

/// <summary>
/// Registration for the Blazor-specific parts of RevalQuery.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Carries the queries a prerender resolved across to the interactive client, so the client
    /// renders from data the server already fetched instead of fetching the same keys again.
    /// </summary>
    /// <remarks>
    /// <para>Register this in both hosts of a WebAssembly app, and on the server of a Server
    /// app: the prerender writes the payload and the interactive render reads it, and those are
    /// different processes for WebAssembly.</para>
    /// <para>The transfer joins any <see cref="IQueryPersistence"/> you register rather than
    /// replacing it. Call this first to have transferred data preferred over a durable store's,
    /// which is usually right: it was fetched during this very request.</para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="serializerOptions">
    /// Options whose TypeInfoResolver covers every query result type you want transferred.
    /// Required, and required to have a resolver: the framework offers no way to hand a
    /// JsonTypeInfo to PersistAsJson, so the library serialises query data itself, and
    /// reflection-based serialisation fails silently in trimmed WebAssembly. See ADR 0004.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializerOptions"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="serializerOptions"/> has no TypeInfoResolver, which would leave the
    /// transfer working in development and silently empty in a trimmed publish.
    /// </exception>
    public static IServiceCollection AddRevalQueryPrerenderTransfer(
        this IServiceCollection services,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);

        if (serializerOptions.TypeInfoResolver is null)
        {
            throw new ArgumentException(
                "The prerender transfer needs a JsonSerializerOptions with a TypeInfoResolver, " +
                "normally a source-generated JsonSerializerContext covering your query result " +
                "types. Without one, serialisation falls back to reflection, which is trimmed " +
                "away in a published WebAssembly app and would leave the transfer silently " +
                "empty there while working in development.",
                nameof(serializerOptions));
        }

        services.AddScoped<PrerenderTransfer>(sp => new PrerenderTransfer(
            sp.GetRequiredService<PersistentComponentState>(),
            sp,
            serializerOptions));

        services.AddScoped<IQueryPersistence>(sp => sp.GetRequiredService<PrerenderTransfer>());

        return services;
    }
}
