using Microsoft.AspNetCore.Components;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Blazor;

/// <summary>
/// Renders nothing. Connects a <see cref="RevalHooks"/> to the component that reads through
/// it: observer changes re-render that component, each completed render sweeps the hooks, and
/// removing the renderer releases them.
/// </summary>
/// <remarks>
/// <para>Place it once at the top level of the owning component's markup, outside any conditional
/// or wrapper component, as <c>&lt;RevalRenderer Component="this" Hooks="Reval" /&gt;</c>, with the
/// hooks injected (<c>@inject RevalHooks Reval</c>). Removing the renderer releases the hooks for
/// good, and nothing re-attaches them, so its lifetime must equal the component's: an
/// <c>@if</c>, <c>@foreach</c>, <c>AuthorizeView</c>, <c>ErrorBoundary</c> or tab panel around it
/// would end that lifetime early. Razor allows several root nodes, so this is always possible. A
/// wrapper that always renders its child content still works, but is not recommended.</para>
/// <para>The sweep runs in this component's <c>OnAfterRender</c>, which is what makes release
/// follow renders and nothing else. See <see cref="RevalHooks"/> for the release rule and its
/// two known limitations: a hidden branch keeps its queries until it renders again, and a call
/// site read in a page and in an async-loading child with different keys can thrash.</para>
/// <para>The hooks live until this component is disposed, or until a different instance is
/// passed in, which releases the previous one.</para>
/// </remarks>
public sealed class RevalRenderer : ComponentBase, IDisposable
{
    /// <summary>The component to re-render when a query or mutation changes, normally <c>this</c>.</summary>
    [Parameter, EditorRequired] public IHandleEvent Component { get; set; } = default!;

    /// <summary>The hooks the component reads through, normally injected.</summary>
    [Parameter, EditorRequired] public RevalHooks Hooks { get; set; } = default!;

    private RevalHooks? _attached;
    private IDisposable? _handle;
    private bool _isDisposed;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (Component is null)
            throw new InvalidOperationException($"{nameof(RevalRenderer)} requires a {nameof(Component)}.");

        if (Hooks is null)
            throw new InvalidOperationException($"{nameof(RevalRenderer)} requires {nameof(Hooks)}.");

        if (ReferenceEquals(_attached, Hooks)) return;

        _handle?.Dispose();
        _handle = null;
        _attached = null;

        var hooks = Hooks;
        _handle = hooks.Attach(() => _ = InvokeAsync(() =>
            _isDisposed ? Task.CompletedTask : Component.HandleEventAsync(EventCallbackWorkItem.Empty, null)));
        _attached = hooks;
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender) => _attached?.RenderCompleted();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _handle?.Dispose();
    }
}
