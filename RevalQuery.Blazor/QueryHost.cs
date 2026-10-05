using Microsoft.AspNetCore.Components;
using RevalQuery.Core.Scope;

namespace RevalQuery.Blazor;

/// <summary>
/// Renders nothing. Connects a <see cref="QueryScope"/> to the component that created it:
/// observer changes re-render that component, each completed render sweeps the scope, and
/// disposing the component disposes the scope.
/// </summary>
/// <remarks>
/// <para>Place it once in the owning component's markup, passing the scope made with
/// <see cref="QueryScopeExtensions.CreateScope(RevalQuery.Core.QueryClient, IHandleEvent)"/>.
/// It must render whenever its parent does, which it does for any parent render: the scope is
/// a reference type, so Blazor hands it over again every time.</para>
/// <para>The sweep runs in this component's <c>OnAfterRender</c>, which is what makes release
/// follow renders and nothing else. See <see cref="QueryScope"/> for the release rule and its
/// two known limitations: a hidden branch keeps its queries until it renders again, and a call
/// site read in a page and in an async-loading child with different keys can thrash.</para>
/// </remarks>
public sealed class QueryHost : ComponentBase, IDisposable
{
    /// <summary>The scope to connect. Created with <c>Client.CreateScope(this)</c>.</summary>
    [Parameter, EditorRequired] public QueryScope Scope { get; set; } = default!;

    private QueryScope? _attached;
    private IDisposable? _link;
    private bool _isDisposed;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (Scope is null)
            throw new InvalidOperationException($"{nameof(QueryHost)} requires a {nameof(Scope)}.");

        if (ReferenceEquals(_attached, Scope)) return;

        if (!QueryScopeExtensions.TryGetOwner(Scope, out var owner))
            throw new InvalidOperationException(
                $"The scope given to {nameof(QueryHost)} has no owner. Create it with Client.CreateScope(this), " +
                "so the host knows which component to re-render.");

        _link?.Dispose();
        _attached?.Dispose();
        _attached = Scope;

        _link = Scope.Attach(() => _ = InvokeAsync(() =>
            _isDisposed ? Task.CompletedTask : owner.HandleEventAsync(EventCallbackWorkItem.Empty, null)));
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender) => _attached?.RenderCompleted();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _link?.Dispose();
        _attached?.Dispose();
    }
}
