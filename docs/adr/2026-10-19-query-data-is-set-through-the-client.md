# Query data is set through the client, and a set is fresh data arriving now

Status: Accepted
Date: 2026-10-19

## Context
`IQueryState<T>.Data` and `Exception` have public setters, and the concrete `QueryState`,
which `FindQuery<T>` and `GetOrCreateQuery` return, also exposes setters for `Status`,
`FetchStatus` and the three option records. The optimistic-update recipe in the Blazor README
assigns `Data` directly. That assignment takes no lock, though `ApplyFetched` writes data,
status and timestamp under `_dataGate`, and the thread-safety ADR promises the client locks
internally. It also raises no notification, so other components watching the key keep the old
value. It leaves a pending query pending, moves no timestamp, and is overwritten by a restore
that answers later. The recipe looks correct only because the mutation's own state change
re-renders the component that ran it.

## Decision
- Query state is read-only outside the client. Every setter on `QueryState` becomes internal,
  and `FindQuery` and `GetOrCreateQuery` return `IQueryState<T>`.
- Data is **set** (see `CONTEXT.md`) through `RevalClient.SetQueryData(queryOptions, updater)`,
  with an updater `Func<T?, T?>` and a plain-value overload. It is synchronous.
- An updater returning `null` sets nothing. Otherwise a missing query is created from the
  options, which apply only when this call creates it, as with `QueryAsync`.
- A set records, under `_dataGate` and as one step: the data, `Resolved`, a cleared error, a
  cleared invalidated flag, and a timestamp of now or a caller-supplied `updatedAt`. It then
  notifies observers.
- A set never starts a fetch and never cancels one. A caller that must stop an in-flight fetch
  from overwriting the set calls `CancelAsync` first.
- A set counts as settling, so a restore still reading the store is refused when it answers.
- A set is persisted through `SaveAsync`. Saves are queued per query, so the last set is the
  last saved, and a failure is swallowed as a fetch's is.

## Considered options
- **Keep the `Data` setter and make it lock and notify**: rejected. A property assignment would
  hide a lock, a status change, a timestamp, a notification and a save, and the other public
  setters (`Status`, `FetchStatus`) would remain the same defect.
- **Set without moving the timestamp**, as the `IsStale` remarks justified: rejected. That choice
  existed only to excuse the uncontrolled setter. TanStack's `setQueryData` sets
  `dataUpdatedAt`, and a set value that reports year-one freshness is wrong data in a public,
  persisted field.
- **`SetQueryData(key, updater)`, updating only existing queries**: rejected. A query cannot
  exist here without its handler, and seeding a detail query from a list is the main reason to
  create one.
- **`Task SetQueryDataAsync` that completes after the save**: rejected. Ordering would depend on
  every caller awaiting, and a discarded task hides failures. TanStack's is synchronous.
- **A prefix-wide set (`setQueriesData`)**: left out. It needs query filters, which should be
  designed once for invalidate, cancel and set together.

## Consequences
- Breaking on 0.x: the setters go, and the README's optimistic recipe is rewritten around
  `SetQueryData` and the mutation callback context
  (`mutation-callbacks-take-one-context`).
- This qualifies the reasoning, not the decision, of `the-imperative-api-is-one-method`. Its
  first staleness rung tests "never fetched" rather than "holds no data" because a set left the
  clock alone. A set now moves the clock, so the two readings agree again. The rung stays as
  written. The `IsStale` remarks that cite the setter are updated.
- A value set on a static query is fresh, so a mutation's `Invalidate` on settle does not
  refetch it. TanStack behaves the same way.
- Future code that puts data into a query goes through the set path. Nothing outside the client
  assigns query state.
