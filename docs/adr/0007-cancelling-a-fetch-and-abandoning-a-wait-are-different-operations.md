---
status: accepted
---

# Cancelling a fetch and abandoning a wait are different operations

`QueryClient` now offers both, and they are deliberately not the same thing.

`CancelAsync(key)` cancels: the in-flight fetch of every query under the key prefix stops, its
result is discarded even if the handler produced one anyway, and the call completes once they
have all unwound. `FetchQueryAsync`'s `CancellationToken` abandons: the caller's await ends with
an `OperationCanceledException` while the fetch runs on and its result still lands in the
registry.

The reason they differ is that a fetch is shared. Callers arriving while one is running join it
rather than starting a second, and a query's data belongs to the registry, not to whoever asked
for it. One caller walking away is not grounds to take the fetch from the others or to throw
away the entry that the next caller, or the same caller a keystroke later, will want. An
autocomplete that abandons a request on the next keystroke still wants the answer cached for the
backspace that follows. Cancelling, by contrast, is a statement about the query itself: this
fetch should not land.

## Considered options

Cancelling the fetch from `FetchQueryAsync`'s token was the obvious reading of the parameter and
is what a reader will expect. We rejected it because it destroys the cache entry that is the
point of fetching at all, and because it lets one of several joined callers cancel the others'
work. The XML docs say plainly that the token abandons the wait, since the surprising behaviour
is the one that needs saying.

Self-cancellation, where a query cancels its own fetch once nothing is observing it, was
considered and rejected. TanStack Query's docs settle it: "queries that unmount or become
unused before their promises are resolved are _not_ cancelled". The same reasoning applies,
more strongly, because the result of such a fetch is exactly the warm cache entry a remount is
about to read.

Exact-key matching for `CancelAsync` was rejected in favour of prefix matching, so it covers the
same set as `Invalidate` and matches TanStack's `cancelQueries`. The two are used together on
the same key in an optimistic update, and a pair that disagreed about what a key means would be
a trap.

A synchronous `Cancel` was what the library had, and it is gone rather than kept alongside.
Cancelling without waiting is not enough for the case that motivates cancelling at all: a
refetch started before a mutation lands afterwards and overwrites the optimistic value, and
knowing that cannot happen means waiting for the unwind. `IQueryState.Cancel()` remains as the
low-level trigger for one query, for callers that do not need to wait.

## Consequences

A handler that never reads its `CancellationToken` is now cancellable. Its result is dropped at
the point where it would have been applied, which is what TanStack does by cancelling the
retryer so the value lands nowhere. This is the behaviour that makes `Cancel` a cancellation
rather than a suggestion.

A cancelled query keeps the data it already had and records no error. The library holds no
history, so there is nothing to revert to and nothing to report: a fetch that was stopped never
learned anything.

Reaching an in-flight fetch from the registry needed a non-generic face for the worker, so
`RegistryNode.Worker` is typed `IQueryWorker` rather than `IDisposable`. Both are internal.

`QueryClient.Cancel` is removed. That is a breaking change for 0.4.0.
