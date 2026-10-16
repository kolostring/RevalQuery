# Library time comes from an injected TimeProvider, and timers are armed synchronously

Status: Accepted
Date: 2026-10-16

## Context
Four behaviours depend on time: `RefetchInterval` polling, retry backoff, `GcTime` eviction and
`StaleTime` expiry. The library reads `DateTime(Offset).UtcNow` and calls `Task.Delay` directly
for all of them, so no test can control them. The tests can only sleep and hope, and the suite
is flaky because of it (`docs/research/2026-10-16-reliable-tests.md`). TanStack Query makes the
same behaviours deterministic with global fake timers (`vi.useFakeTimers()`, which also mocks
`Date`). .NET has no global clock to swap, so the clock has to be passed in.

## Decision
- `RevalQueryOptions.TimeProvider` is public and defaults to `TimeProvider.System`. Every read
  of "now" (staleness, GC expiry, persisted freshness) goes through `GetUtcNow()`, and every
  wait goes through the provider.
- **Timers are armed synchronously**, at the call that makes them due. The poll timer is created
  with `TimeProvider.CreateTimer` under the worker's gate in `StartPolling` and disposed where
  polling stops. The GC sweep timer is created the same way when the policy starts. Each `ITimer`
  is held in a field.
- Retry backoff waits with `Task.Delay(delay, timeProvider, cancellationToken)`, armed by the
  attempt that just failed.
- Core takes no new package: `TimeProvider` is in the BCL on every framework we target.
  `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) is a test-only reference.

## Considered options
- **Pass the provider to the existing `Task.Delay` calls inside `Task.Run` loops**: rejected
  because the delay is then armed at an unknown moment after `Task.Run`. A test that advances the
  fake clock before the delay exists loses the advance, and `FakeTimeProvider` can't report
  which timers are armed (dotnet/extensions#7515) or await what `Advance` started (#6404).
- **`PeriodicTimer(interval, timeProvider)`**: rejected because it delivers one tick per wait
  under `Advance` and can't be advanced from its own callback (dotnet/extensions#6810). A
  periodic `CreateTimer` fires once per elapsed period within a single `Advance`.
- **An internal provider, settable only through `InternalsVisibleTo`**: rejected. Applications
  testing their own components need the same control, and TanStack exposes its timer backend
  (`timeoutManager`) publicly. It would also need `InternalsVisibleTo` for every test project.
- **No clock seam, with the time-driven behaviours documented as untestable**: rejected because it
  leaves polling, backoff, GC and staleness permanently covered only by sleep-based tests.

## Consequences
- Timer callbacks run on a timer thread in production, as the `Task.Delay` continuations do
  today, and on the thread that calls `Advance` in tests. Both are covered by the
  `supported-hosting-models-and-the-thread-safety-contract` ADR, which already has the eviction
  policy call into the client from a timer thread.
- A poll callback that fires while a fetch is in flight joins it, as today: one fetch in flight
  per query.
- Staleness is decided at the boundary with `>=`, so `StaleTime(0)` is stale even when the clock
  has not moved (TanStack `isStaleByTime`). Under a frozen fake clock, `>` would make every
  zero-stale-time query fresh.
- GC keeps its periodic sweep, so eviction lands between `GcTime` and `GcTime + GcInterval`
  after release. A fake-clock test advances by both. Per-key GC timers, which TanStack uses,
  would remove `GcInterval` and are a separate decision.
- New time-dependent code takes its clock from `RevalQueryOptions.TimeProvider`. Code under
  `RevalQuery.Core` never calls `DateTime.UtcNow`, `DateTimeOffset.UtcNow` or the overloads of
  `Task.Delay` that take no provider.
