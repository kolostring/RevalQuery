# A worker stays alive while a run is claimed or in flight

Status: Accepted
Date: 2026-10-16

## Context
`QueryAsync` looks up a query's worker under the client lock, then runs it outside the lock.
Two things can take the worker away in that gap or after it:

- **Release.** Another caller settles and releases the unobserved worker. The run then reports
  `NotRun`, and `QueryAsync` retries with a fresh worker. Commit `a700df3` capped the retries
  at three, after which it throws `InvalidOperationException("… could not be run …")`. Under
  contention, three losses in a row are reachable, so a caller gets an exception that says nothing
  about its query.
- **Eviction.** `HandleEviction` checks only for observers. It removes an unobserved query and
  cancels its fetch even while a `QueryAsync` is awaiting that fetch, so an awaited fetch can be
  cancelled by GC.

TanStack has neither problem. `fetchQuery` runs on the query object itself, and `optionalRemove`
removes a query only when it has no observers **and** its `fetchStatus` is `idle`.

## Decision
- A worker is **held** while it has observers, a run that has been claimed but not yet started,
  or a fetch in flight.
- The lookup that `QueryAsync` makes under the client lock **claims** the worker. The claim
  becomes the in-flight fetch, or a join of it, when `RunAsync` starts, and is dropped if the
  run is not needed.
- Release refuses a held worker and defers until the last hold ends, as `TryRelease` already does
  for an in-flight fetch.
- **Eviction refuses a held worker too.** An evicted query that is held is re-registered for
  eviction when the hold ends, rather than removed under the run.
- With release unable to take a claimed worker, `QueryAsync` never sees `NotRun` from a live
  client. The retry loop, `MaxRunAttempts` and the "could not be run" exception are removed. A
  disposed client still throws `ObjectDisposedException`.

## Considered options
- **Keep the three-attempt bound and document the throw**: rejected because the failure isn't
  about the caller's query, and no caller can act on it.
- **Retry `NotRun` without a limit**: rejected. It replaces a spurious exception with
  potentially unbounded latency, since a caller can keep losing while others keep settling
  first. It also leaves eviction able to cancel an awaited fetch.
- **A larger fixed bound**: rejected because it makes the failure rarer without making it
  meaningful.

## Consequences
- A worker now has three reasons to stay alive instead of two. The claim count is changed only
  under the client lock, and the worker reads it under its own gate.
- `RevalClient` can no longer cancel a fetch through GC. Cancelling stays the job of
  `CancelAsync` (`cancelling-a-fetch-and-abandoning-a-wait-are-different-operations`).
- A query whose `GcTime` elapses during a long unobserved fetch is evicted after the fetch
  settles, not during it, which matches TanStack.
- Tests pin both rules deterministically with `ManualEviction` and a gated handler. Release or
  evict between the lookup and the run, and while the fetch is in flight, then assert that the
  caller gets the data and the handler ran once.
