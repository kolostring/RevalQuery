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

## Mutations are created and re-optioned the same way

`UseMutation` built a `MutationState` and a `MutationObserver` by hand on the first render and
returned the slot's state on every one after, without looking at the options again. A callback
that closed over a value the render had changed kept the value from the first render. That is
the defect `ApplyOptions` fixed for queries, left standing for mutations, and it had the same
cause: the caller owned the construction, so nothing in Core was responsible for what came next.

`QueryClient.CreateMutation(options, onChanged)` returns the observer, with the client's own
service provider handed to handlers, so a component needs no `IServiceProvider` of its own to
build one. The `MutationObserver` and `MutationState` constructors went internal for the reason
the query observer's did. `QueryClient.Observe(ref slot, options, onChanged)` has a mutation
overload that creates on the first call and calls `MutationObserver.SetOptions` on the rest.
There is no key, so a mutation never moves: `SetOptions` is the re-apply half alone.

`MutationObserver.SetOptions` follows TanStack, whose `setOptions` replaces `this.options` and,
if the current mutation is pending, calls `currentMutation.setOptions`. What a run reads is then
decided by when it reads it. `Mutation.execute` reads `mutationFn` on every retry attempt and
`onMutate`, `onSuccess`, `onError` and `onSettled` at the moment each fires, and it captures
`retry` and `retryDelay` once, when the retryer is created at the start of the run. Per-call
`mutate()` callbacks are a separate set and unaffected.

TanStack builds a `Mutation` for every `mutate()` call, so each run already has options of its
own, and only the latest is updated. RevalQuery has one `MutationState` for every concurrent
run, because the state is what a component reads and `Data` and `Status` describe the latest.
Reading a plain `_options` field lazily would therefore push the new options into every run in
flight, where TanStack's older mutations keep theirs. Each `ExecuteAsync` now takes a holder of
its own, seeded from the state's current options, and `SetOptions` replaces the state's options
and the holder of the latest run that is still pending. A run is pending until its last callback
has returned, as TanStack's is, so options set during `OnSettled` still reach it. `Reset` drops
the latest run, as `reset()` drops the current mutation. Retry options are read from what the
run started with, once.

Nothing else on the per-run holder is shared: the state's version counter still decides which
run writes `Data` and `Status`, and per-call `MutateOptions` still fire for the latest run only.
Both were that way before and are untouched.

`MutationState` and `MutationObserver` no longer have a public constructor. Breaking for 0.4.0,
with the changes above.

## Later

ADR 0010 removed `QueryComponentBase`. What it did here, holding one slot per query and calling
`Subscribe` on a new one and `SetOptions` on an existing one, is now `RevalHooks`, which holds an
observer per query key and does the same two things. The observer is unchanged.
