using Microsoft.AspNetCore.Components;
using RevalQuery.Core.Hooks;

namespace RevalQuery.Blazor;

/// <summary>
/// Renders nothing. Connects a <see cref="RevalHooks"/> to the component that reads through
/// it: observer changes re-render that component, each completed render sweeps the hooks, and
/// removing the renderer releases them.
/// </summary>
/// <remarks>
/// <para>Place it once in the owning component's markup, as
/// <c>&lt;RevalRenderer Component="this" Hooks="Reval" /&gt;</c>, with the hooks injected
/// (<c>@inject RevalHooks Reval</c>). It may sit inside another component's child content: the
/// component that is re-rendered is the one named by <see cref="Component"/>, not its parent.
/// It must render whenever its parent does, which it does for any parent render: the hooks instance is
/// a reference type, so Blazor hands it over again every time.</para>
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
        _attached = Hooks;

        _handle = Hooks.Attach(() => _ = InvokeAsync(() =>
            _isDisposed ? Task.CompletedTask : Component.HandleEventAsync(EventCallbackWorkItem.Empty, null)));
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
