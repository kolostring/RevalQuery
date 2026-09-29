---
status: accepted
---

# We support multi-user server processes, so QueryClient locks internally

RevalQuery is a client-side UI library, which makes its internal locking look like
over-engineering. It is not. We support Blazor Server and server prerendering, both of
which run components in a process shared by many users on many threads, so `QueryClient`
synchronises its own state and its registry is scoped per user session, never shared.

The trap is that targeting WebAssembly only does not avoid this. The render modes
documentation states that interactive render modes, WebAssembly included, "support
prerendering by default", so a standard `@rendermode InteractiveWebAssembly` app runs
components on the multi-user server before the client takes over. Declaring
WebAssembly-only would require also declaring prerendering unsupported, which is the
default template.

## Considered options

Declaring WebAssembly-only was the rejected option, and it had a genuine payoff we gave
up: WebAssembly is effectively single-threaded, so we could have documented thread
affinity and removed all locking. We rejected it because prerendering reintroduces the
multi-threaded case anyway, because `RevalQuery.Core` ships as a standalone package with
no Blazor dependency and will be used server-side regardless, and because a registry
shared across users leaks one user's data to another.

Thread affinity, where callers must marshal onto one thread, was also rejected. It is
not honestly available while our own eviction policy calls back into `QueryClient` from
a timer thread. The library would be breaking its own rule.

TanStack Query reached the same conclusion from the other direction: its SSR guidance is
one QueryClient per request, because a shared one "makes the cache shared between all
requests and means all data gets passed to all users".

## Consequences

`QueryClient` takes one lock, and it covers the registry: which keys exist, what state
sits at each, and which worker drives it. It does not cover a query's data or its status.
`Data`, `Status` and `FetchStatus` are plain properties with no lock and no volatile, and
they are read without synchronisation.

That is correct, but for a narrower reason than a wide lock would give, so the invariant
is worth naming. **A query has at most one fetch in flight at a time**, enforced by
`QueryWorker`'s `_inFlight`: a second caller joins the running fetch rather than starting
another. One writer means no torn interleaving of two results, and the writes a reader
might miss are ones the following `NotifyChanged` will bring it back for. Where an update
must be seen whole, `QueryState` takes `_dataGate` and does the whole thing under it:
`ApplyFetched`, `TryRestore` and `Snapshot` each pair the data with its timestamp in one
step, which is what stops a restore landing between a fetch's data and its clock, or a
snapshot carrying one fetch's data with another's time.

Observer callbacks are deliberately left unsynchronised, because `QueryComponentBase`
already routes them through `InvokeAsync`, which is the renderer's job rather than ours.

The registry, the eviction policy and `QueryClient` all share one lifetime. Registering
any of them as a singleton alongside a scoped `QueryClient` is a data leak between users,
not merely a lifetime mismatch.

The eviction policy starts lazily on first use rather than through a hosted service,
because WebAssembly has no host to start one.
