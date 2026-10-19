# A set's updater reruns when the data moved under it

Status: Accepted
Date: 2026-10-19

## Context
`SetQueryData(options, updater)` reads the current data, runs the caller's updater, then applies
the result under `_dataGate`. Only the apply holds the lock. Two updaters on one key at the same
moment can both read the same value, and the second apply discards the first's change. The
thread-safety ADR promises that the client locks internally, and multi-user server processes
are a supported host, so a lost update is a defect, not a documented limitation. TanStack
avoids the question only because JavaScript runs one updater at a time.

## Decision
- The updater runs outside `_dataGate`. The set reads the data with a version, runs the
  updater, and applies only if the version is unchanged. Otherwise it reruns the updater on
  the newer data.
- Every write to a query's data (a set, a fetch landing, a restore) moves the version.
- An updater returning `null` on any attempt sets nothing, as before.
- An updater may therefore run more than once per call, and must be a pure function of its
  input. The XML docs say so.

## Considered options
- **Run the updater under `_dataGate`**: rejected. User code would run under the lock, and an
  updater that calls back into the client (a read, another set) could deadlock or block a
  fetch landing on another thread.
- **Keep the gap and document it**: rejected. It breaks the thread-safety contract for the one
  call whose purpose is a read-modify-write.

## Consequences
- Two concurrent updaters both take effect, in some order.
- An updater with side effects may repeat them, and the docs warn against that.
