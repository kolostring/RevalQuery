---
status: accepted
---

# Loading covers restores, but FetchStatus stays untouched

A query waiting on a persistence restore has no data and is not fetching, so components
rendered their empty state for the duration and then flipped to data. We fixed that by
widening what `IsLoading` reports, to "no data yet and work in flight, fetch or restore".
We deliberately did **not** model restoring as a `FetchStatus` value.

`FetchStatus` is load-bearing beyond display. `CanFetch` is `FetchStatus == Idle &&
IsEnabled`, so reporting `Fetching` during a restore turns `CanFetch` false. An
`Invalidate` arriving mid-restore is then dropped, the restore lands and writes the stored
timestamp over the `MinValue` that `NotifyInvalidated` wrote, nothing is stale any more,
and the query never refetches. The user is left on stale data with no way to tell.

Today that cannot happen, because `CanFetch` is true throughout the restore: the
invalidation fetches immediately and `TryRestore` then declines on `_hasSettled`.
`RevalQuery.Tests/InvalidateDuringRestoreTests.cs` pins exactly this. If you are here
because that test failed, something started reporting `Fetching` during a restore.

## Considered options

Adding `FetchStatus.Restoring` was the main alternative and is the one a future reader
will propose. TanStack settles it against us. Their `fetchStatus` already carries three
values, `fetching`, `paused` and `idle`, so they were plainly willing to extend the enum,
and they still did not add a restoring value. Their persistence docs are explicit that
queries "will just be put into `fetchingState: 'idle'` until data has been restored", with
restoration surfaced separately through a `useIsRestoring` hook. Adding an enum value is
also a breaking change for any consumer switching on it.

Exposing `IsRestoring` and leaving `IsLoading` alone is what TanStack actually does, and we
rejected it only on defaults. It requires every consumer to write
`IsFetching || IsRestoring`, and forgetting reproduces the blank flash the change exists to
remove. `IsRestoring` remains available for anyone who wants to distinguish waiting on disk
from waiting on the network.

One structural difference made that choice available to us and not to them. TanStack
restores the whole client once, so a single global hook fits. Restores here are per query,
created lazily with the query, so restoring is already per-query state and folds naturally
into a per-query `IsLoading`.

## Consequences

`FetchStatus` keeps its glossary meaning: whether a fetch is running. A restore is not a
fetch. `CanFetch` is unchanged, so invalidation during a restore keeps working by
construction rather than by a second mechanism defending it.

`IsLoading` no longer equals `IsFetching && IsPending`. Anything asserting that identity is
now wrong.

The restore is not given a timeout. Adapters must complete or fault, never hang, and
`IQueryPersistence` says so. A hanging adapter stalls its query, which is the adapter's bug.
