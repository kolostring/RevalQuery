using Microsoft.AspNetCore.Components;
using RevalQuery.Core.Tracking;

namespace RevalQuery.Blazor;

/// <summary>
/// Renders nothing. Connects a <see cref="QueryTracker"/> to the component that reads through
/// it: observer changes re-render that component, each completed render sweeps the tracker, and
/// removing the renderer releases the tracker.
/// </summary>
/// <remarks>
/// <para>Place it once in the owning component's markup, as
/// <c>&lt;QueryRenderer Component="this" Tracker="Q" /&gt;</c>, with the tracker injected
/// (<c>@inject QueryTracker Q</c>). It may sit inside another component's child content: the
/// component that is re-rendered is the one named by <see cref="Component"/>, not its parent.
/// It must render whenever its parent does, which it does for any parent render: the tracker is
/// a reference type, so Blazor hands it over again every time.</para>
/// <para>The sweep runs in this component's <c>OnAfterRender</c>, which is what makes release
/// follow renders and nothing else. See <see cref="QueryTracker"/> for the release rule and its
/// two known limitations: a hidden branch keeps its queries until it renders again, and a call
/// site read in a page and in an async-loading child with different keys can thrash.</para>
/// <para>The tracker lives until this component is disposed, or until a different tracker is
/// passed in, which releases the previous one.</para>
/// </remarks>
public sealed class QueryRenderer : ComponentBase, IDisposable
{
    /// <summary>The component to re-render when a query or mutation changes, normally <c>this</c>.</summary>
    [Parameter, EditorRequired] public IHandleEvent Component { get; set; } = default!;

    /// <summary>The tracker the component reads through, normally injected.</summary>
    [Parameter, EditorRequired] public QueryTracker Tracker { get; set; } = default!;

    private QueryTracker? _attached;
    private IDisposable? _handle;
    private bool _isDisposed;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (Component is null)
            throw new InvalidOperationException($"{nameof(QueryRenderer)} requires a {nameof(Component)}.");

        if (Tracker is null)
            throw new InvalidOperationException($"{nameof(QueryRenderer)} requires a {nameof(Tracker)}.");

        if (ReferenceEquals(_attached, Tracker)) return;

        _handle?.Dispose();
        _attached = Tracker;

        _handle = Tracker.Attach(() => _ = InvokeAsync(() =>
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
