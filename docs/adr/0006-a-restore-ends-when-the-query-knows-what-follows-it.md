---
status: accepted
---

# A restore ends when the query knows what follows it, not when the store answers

A query with nothing in persistence reported one notification with `IsPending` true and
`IsLoading` false. A component branching on `IsLoading` rendered its empty state for that
frame, then its spinner, then its data. `/loading-and-restoring` in `CachingDemo` showed it
on every load.

The gap was in the ordering. The restore ended the moment the store answered, and the worker
only then woke from awaiting it and decided the query was stale. Between those two, nothing
was in flight:

| time  | event                        | IsRestoring | IsFetching | IsPending | IsLoading |
| ----- | ---------------------------- | ----------- | ---------- | --------- | --------- |
| 0 ms  | query created, restore opens | true        | false      | true      | **true**  |
| 150ms | store answers: nothing there | false       | false      | true      | **false** |
| 150ms | observers notified           | false       | false      | true      | **false** |
| 151ms | worker wakes, starts fetch   | false       | true       | true      | **true**  |

The notification at 150 ms is the blank frame. It is not a missing notification or a stale
read: every value reported is correct for that instant. The instant itself should not exist.

We fixed it by moving where a restore ends. A restore now covers the whole question "what is
this query's data", not just the part answered by the store. It begins when the query is
created and ends once the query knows what follows the stored data: either the stored data
stands, or a fetch has already started because there was none or it was stale. Reading the
store became the first part of a restore rather than the whole of it, and `CONTEXT.md` says
so.

Mechanically, `QueryState` holds a `TaskCompletionSource` for the restore. `IsRestoring` reads
whether it has completed. `TryDeferUntilRestored(Action)` hands the staleness decision to the
restore instead of the worker making it later, and `CompleteRestore()` runs that decision
before completing the source. A fetch that follows a restore is therefore already running,
with its fetch status set, at the moment anything reports the restore over. There is no
instant left to notify in.

## Considered options

Keeping the boundary and suppressing the notification was the obvious smaller change: end the
restore, skip the notification, let the fetch notify instead. We rejected it because the blank
frame is not only a notification. Anything reading `IsLoading` in that window, a component
re-rendering for an unrelated reason, a test, a future `useIsRestoring` equivalent, sees a
query that is pending and not loading. Suppressing one observer path leaves the state itself
lying.

Merging `IsRestoring` and `IsFetching` into one status was raised again and rejected again, for
the reason in ADR 0005 and one more: a restore and a fetch can genuinely be in flight at once,
which `InvalidateDuringRestoreTests` pins. No single enum expresses that.

Exposing the restore's task on `IQueryState` was rejected deliberately. `RestoreCompleted` is on
the concrete `QueryState`, where tests and the client reach it, and not on the interface a
consumer holds. Handing a consumer a task to await invites exactly the hang that
`IQueryPersistence` forbids adapters from causing.

## Consequences

`IsLoading` is still `(IsFetching || IsRestoring) && IsPending`. The predicate did not change;
what changed is when `IsRestoring` goes false. ADR 0005 stands as written.

`QueryWorker` no longer takes or awaits a restore task, and `RegistryNode` no longer holds one.
The decision that needed the restore moved into the state, so the worker has nothing left to
wait for.

A restore that ends with no observer subscribed defers no decision and simply ends when the
store answers. The first subscriber to arrive afterwards decides for itself, immediately,
because there is no longer a restore to defer to.

A restore that has begun ending refuses to take over any further decision, and says so by
returning false. That needs a field of its own: the restore's completion stays incomplete while
the decision runs, which is the whole point of the ordering, so "ending" is not derivable from
it. Without the field, a decision handed over in that window is stored and never made, and a
query whose only decision was to fetch never fetches at all. `RestoreBoundaryTests` covers the
window directly.

A deferred decision that throws still ends the restore. Nothing else would, and a restore left
open never ends, so the query would report itself loading forever.

`RestoreBoundaryTests` guards all of this. If you are here because it failed, something moved
the boundary back.
