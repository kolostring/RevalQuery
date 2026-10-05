---
status: accepted
---

# Observers own their options and key switching; the caller owns the slot

A render hands its options to an observer, and the observer decides which query it watches. The
caller holds one slot per query for the life of the component and never compares a key.

`QueryObserver<TRes>` was a subscription to one query, fixed at construction. Its `Query`
property had no setter, so a key that changed under a component meant a different observer, and
somebody had to notice. That somebody was `QueryComponentBase.UseQuery`, which compared the
slot's current key against the new one, disposed the observer on a mismatch, and subscribed
afresh. The bookkeeping lived in the Blazor project, so a component written against Core alone
had to reproduce it: hold the observer, compare keys, dispose, resubscribe, and re-point every
reference to the old observer's `Query`. Nothing in Core said that was the job.

`QueryObserver<TKey, TRes>.SetOptions(options)` moves it inside the observer. The same key
re-applies the options to the query the observer is on, which is what `ApplyOptions` did. A
different key moves the observer to the query that key names, and `Query` changes with it.
`QueryClient.Observe(ref slot, options, onChanged)` is the two steps a render takes: subscribe
when the slot is empty, `SetOptions` when it is not.

The observer gained its `TKey` for this, because `SetOptions` takes options and options carry
the key type. The constructor went internal. An observer built by hand could not be moved,
since the mechanics belong to the client, and the only construction that worked was already
`Subscribe`.

## The order of a move

1. Run the plugin pipeline over the options and resolve or create the new query, under the
   registry lock. A result type clash throws here, before anything has moved, so the observer
   is left on its old query with its subscription and its handler intact.
2. Attach to the new query in the same lock hold: hook the change handler, subscribe, take the
   worker. This is `Subscribe`'s own reason for doing the three at once. An eviction landing
   between finding a query and subscribing to it would drop the state the observer is about to
   report, and the next subscriber to the key would get a second one.
3. Release the old query exactly as `Dispose` does: unhook, unsubscribe. The last-subscriber
   path stops its polling, disposes its worker, and puts it on the eviction list.
4. Adopt the options on the new worker and fetch if its data is stale, as a new subscription
   does.

The plan was to detach before attaching. Attaching first costs nothing, because the observer
forwards notifications only from the query it currently holds, and it removes the interval in
which an observer is on no query at all while the registry is free to change under it.

The old query's in-flight fetch is not cancelled. Other callers may be joined to it, disposing a
worker has never cancelled one for the same reason (ADR 0007 keeps cancelling a fetch apart from
releasing it), and a user flicking between two keys
should find the first one's answer cached when they come back inside `GcTime`. The old worker
cannot be disposed under a running fetch, so it is let go when that fetch settles, through the
release the registry already makes.

An observer-level lock serialises moves and disposal of one observer, so `Query` and the
handler that is forwarding change notifications move together. Notifications are forwarded
through a handler made for each attachment that drops anything arriving after the observer has
left that query. Without it, a notification already on its way from the old query would reach
the component as though the new one had changed. `Query` itself is a single volatile read and
does not take the lock. A reader that could block behind a move in progress is a deadlock waiting
for the right call order, and a reference read has no half-moved answer to protect. Lock order
is observer then registry and never the reverse.

The same-key path is the one every render takes, so it stays as cheap as `ApplyOptions` was. It
adopts the options and nothing else. A fetch follows only when the query has just become
enabled, and polling restarts only when the interval changed. Both decisions were already the
worker's, and a test now pins that an unchanged key with a different `StaleTime` fetches
nothing.

`SetOptions` after the observer or the client is disposed throws `ObjectDisposedException`. It
is a call a live render makes, the class of entry point the client already reports loudly, as
opposed to teardown, which stays silent. `Dispose` is idempotent.

## Precedent

TanStack Query does this. `QueryObserver.setOptions` calls `#updateQuery`, which builds the
query for the new options and, when it differs from the one it holds, runs
`prevQuery.removeObserver(this)` and then `this.#currentQuery.addObserver(this)`. The framework
adapters call `setOptions` on every render and never compare keys. RevalQuery had the observer
class and was missing the method.

## Considered options

A separate `QueryBinding` class, owning a slot's observer and replacing it on a key change,
was rejected. It has the stale-reference problem unchanged: a component still holds the
binding, and the binding holds something that changes under it. It also adds a concept to the
glossary to cover for the one that already exists. The observer is the thing that was
supposed to be the subscription.

`QueryClient.Resubscribe(observer, options)` returning a new observer was rejected. It leaves
the slot bookkeeping with every caller, who must store the return value, remember to dispose
what it replaced, and do both in the right order. That is what `UseQuery` was doing, and it is
what this record exists to stop asking of callers.

## Consequences

`Subscribe` returns `QueryObserver<TKey, TRes>`. `QueryClient.ApplyOptions` is removed, fully
superseded by `SetOptions`, which does the same thing against the same key and the rest
besides. Both are breaking changes for 0.4.0.

`QueryComponentBase.UseQuery` is `SetOptions` on an existing slot and `Subscribe` on a new one.
Its dispose-and-resubscribe branch is gone. A component whose key changed used to unsubscribe
and resubscribe within one render, which ran the eviction bookkeeping both ways; it now runs
it once, in a single move.

`CONTEXT.md` redefines **Observer** as one component's subscription to whichever query its
current options name.
