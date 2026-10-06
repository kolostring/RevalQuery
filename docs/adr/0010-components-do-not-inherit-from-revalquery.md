---
status: accepted
---

# Components do not inherit from RevalQuery; a scope owns what they read

A component reads through a scope it creates, and a `QueryHost` in its markup connects that scope to
the component. Nothing in the component's inheritance chain belongs to this library.

`QueryComponentBase` is a base class, and a component has one. A page already deriving from
`LayoutComponentBase`, from a component library's base, or from an application base that carries its
own services could not use `UseQuery` without giving one of them up, and the ones that could not be
given up were the usual ones. The goal was to drop the inheritance and keep what it did: subscribe
on the first read, re-apply options on every later one, re-render when a query changes, and release
everything when the component goes. ADR 0009 moved the first two into the observer. What was left to
replace is the slot bookkeeping and the lifecycle, and the lifecycle was the part that needed a base
class.

## Decision

`QueryClient.CreateScope()` returns a `QueryScope`, framework-agnostic and in Core. The component
reads with `Q.Query(options)` and `Q.Mutation(options)` on every render, and the scope does what
`UseQuery` did: one observer per query key, created on the first read, handed the render's options
by `SetOptions` on every later one. A state it returns is the observer's own, so it follows the key.

**Identity is a call site holding a set of keys.** A read is identified by the file and line of the
call, or by an explicit slot, and each such site holds the set of query keys it was given. A loop, or
a helper method called twice, is one site holding several keys, and each key has its own observer.
Two reads of one key share an observer for the whole scope however many sites read it; it is
released when no site holds the key. The options of two sites reading one key are last read wins.

**Release happens when a render completes.** `RenderCompleted()` looks at every site that was read
since the previous sweep and releases the keys at that site that were not read, then starts counting
again. A site that was not read at all releases nothing. A key switch is that rule applied to a
site that held one key: the old key is released after the render that read the new one. Reads between
sweeps, from event handlers, from `OnInitialized` or from content that renders in a later batch,
count towards the next sweep. Mutations are never swept. A running mutation has to outlast the render
that started it, and a finished one is cheap, so they live until the scope is disposed.

**The host re-renders through `IHandleEvent`.** `QueryHost` renders nothing. Given the scope, it
attaches with a callback that asks the owning component to render, completes a sweep in
`OnAfterRender`, and disposes the scope with the component. The owner is the `IHandleEvent` handed to
`Client.CreateScope(this)`, held on the Blazor side so that Core has no Blazor types. The callback
calls `owner.HandleEventAsync(EventCallbackWorkItem.Empty, null)` on the renderer's dispatcher.
`ComponentBase` implements that by invoking the callback and calling `StateHasChanged`, which is the
re-render wanted, and a component that implements `IHandleEvent` without `ComponentBase` is
re-rendered by whatever it does for an event. The scope is a reference type, so Blazor hands it to
the host again on every parent render and `OnAfterRender` runs for every one.

Every notification is forwarded, including one raised inside `Query()` or `Mutation()`. Dropping
those assumed the page's own render was the reader, and it is not when the reader is a child, a
popover, a later batch or `OnAfterRender`: the page would then show stale state. The cost is one
extra render of the owner when a read starts a fetch. It settles, because that render re-reads a key
whose state has not changed, which notifies nothing. One raised before any host has attached is kept
and delivered once on attach, so a read in `OnInitialized` does not lose a change to the gap before
the host exists. A scope takes one host at a time, and a `QueryHost` handed a different scope
disposes the one it replaces.

Nothing in the design reads a clock. What is released is a function of which call sites were read
and when renders completed, so a test of it is a test of render cycles and needs no delay.

## Considered options

**Explicit slots only**, as `UseQuery` had `line` and `member` and nothing more. Every read names its
slot. It is correct everywhere and verbose everywhere, for an identity the compiler already knows.
It stays as the overload for the two cases below.

**One slot per call site.** Cheap, and it is what `UseQuery` did. A call site that runs more than
once with different keys, which is every loop and every helper method, then collides: the second key
moves the one observer off the first, and the two fight over it on every render.

**Call order, as hooks do.** Identity by position in the render. It breaks on getters, which a
component reads in any order and any number of times, on conditionals, and on fragments another
component evaluates later.

**Key alone as identity, releasing what was not read since the last sweep.** No call sites at all, one
observer per key. It releases wrongly whatever is rendered late or not at all: a popover or inline
dialog renders in a later batch through a provider, and a grid that loads asynchronously hides its
rows until the data is in. Making it hold up needed a time grace, which is the next option.

**A time grace, or a trailing timer to sweep after it.** Release a key only once it has gone unread
for some interval. It was built as a prototype and it works as far as the interval is longer than
whatever deferred the read, which is not a thing a component can know. It makes behaviour depend on
how fast the machine is, which makes the tests that pin it depend on sleeping, and an unreleased
observer outliving its component is worse than a late one. Rejected on those grounds, not for
effort.

**Attributes, source generators and IL weaving**, to give each read an identity at compile time. The
call site already is one. All three add a build step to a library that otherwise ships as a package
reference.

**A templated `<Query>` component**, with the data in `ChildContent`. A component per query has its
own lifecycle and needs none of this, but a page reading five queries nests five of them and cannot
read two together, and the state is not available outside the markup.

## Evidence

MudBlazor 9.11 `MudTable` and `MudDataGrid` keep their old rows on screen while `ServerData` reloads,
so a read that lives inside a reloading grid is neither absent nor current. `MudPopover` content and
inline `MudDialog` content render in a later batch than the component that declared them, through
their providers. A scheme that decides at the end of the parent's own batch what was not read has
decided too early for both, which is what the key-only and time-grace prototypes ran into.

## Known limitations

These stand until an alternative is found, and are stated in the XML docs and both READMEs.

1. **A hidden branch keeps its queries.** A call site that is not read releases nothing, because the
   sweep cannot tell a branch that is hidden from one that has not rendered yet. Its queries, and any
   polling they do, live until the page is disposed or the branch renders again. Put `Enabled(isVisible)`
   in the options to pause them meanwhile.
2. **A call site read both in the page and inside an asynchronously loading child, with different
   keys, can thrash.** The page's render reads the site with one set of keys and the child's later
   render with another, and each sweep releases what the other read. Use a separate getter for the
   child's read, or an explicit slot.

## Consequences

`QueryComponentBase`, `UseQuery` and `UseMutation` are removed. Breaking for 0.4.0, with the changes
of ADR 0009. A component replaces `@inherits QueryComponentBase` with a `QueryHost` and a scope, and
`UseQuery(...)` with `Q.Query(...)`.

A scope used without a host never re-renders its component and never releases anything. `QueryHost`
refuses a scope that has no owner or is null, with the fix in the message, but a component that
creates a scope and forgets the host has no one to complain.

Two reads on one line share a site. That is harmless for queries, since a site holds a set, and
means one mutation for mutations, which are keyed by site: use `Q.Mutation(key, options)` when each
needs its own.

The explicit slot comes first, `Query(slot, options)`, as `Mutation(key, options)` does, so a string
slot cannot bind to the call-site overload as its file name.

## Revision: injected tracker

The scope was renamed `QueryTracker` and is injected, registered transient by `AddRevalQuery`,
instead of created with `CreateScope(this)`. It stopped being `IDisposable` so that DI does not hold
every instance for the container's lifetime, which in WebAssembly is the app's. Its lifetime is the
handle `Attach` returns: disposing the handle releases every observer, and a released tracker throws
`InvalidOperationException` on reads.

`QueryHost` became `QueryRenderer` and takes the owner as `Component="this"`, because Blazor gives a
child no public, reliable reference to the component whose markup it is in. A component's render-tree
parent is whoever renders the fragment, for example `MudPaper` for content inside it.
