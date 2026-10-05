using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using RevalQuery.Core;
using RevalQuery.Core.Scope;

namespace RevalQuery.Blazor;

/// <summary>
/// Binds a <see cref="QueryScope"/> to the Blazor component that owns it.
/// </summary>
public static class QueryScopeExtensions
{
    // Core knows nothing of Blazor, so the owner lives here, beside the scope rather than in
    // it. Weak, so a scope that is dropped without a host does not keep its component alive.
    private static readonly ConditionalWeakTable<QueryScope, IHandleEvent> Owners = new();

    /// <summary>
    /// Creates a scope whose changes re-render <paramref name="owner"/>, once a
    /// <see cref="QueryHost"/> for it is in the component's markup.
    /// </summary>
    /// <example>
    /// <code>
    /// &lt;QueryHost Scope="Q" /&gt;
    ///
    /// @code {
    ///     [Inject] QueryClient Client { get; set; } = default!;
    ///     QueryScope? _q;
    ///     QueryScope Q =&gt; _q ??= Client.CreateScope(this);
    /// }
    /// </code>
    /// </example>
    /// <param name="client">The client the scope reads from.</param>
    /// <param name="owner">
    /// The component to re-render, normally <c>this</c>. Any <see cref="IHandleEvent"/> works,
    /// so a component that implements it without deriving from <c>ComponentBase</c> is
    /// re-rendered through its own handler.
    /// </param>
    /// <returns>A scope bound to <paramref name="owner"/>, disposed by its <see cref="QueryHost"/>.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public static QueryScope CreateScope(this QueryClient client, IHandleEvent owner)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);

        var scope = client.CreateScope();
        Owners.Add(scope, owner);
        return scope;
    }

    internal static bool TryGetOwner(QueryScope scope, out IHandleEvent owner) =>
        Owners.TryGetValue(scope, out owner!);
}
