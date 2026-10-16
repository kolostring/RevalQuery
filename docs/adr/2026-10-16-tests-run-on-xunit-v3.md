# Tests run on xUnit v3

Status: Accepted
Date: 2026-10-16

## Context
Both test projects use xUnit 2.9. The deterministic suite drives the library's clock with
`FakeTimeProvider` (`library-time-comes-from-an-injected-timeprovider`). That package's README
warns that xUnit v2's `AsyncTestSyncContext` "may lose track of the continuation, causing the
test to become unresponsive" when the code under test uses `ConfigureAwait(false)`, and states
that v3 removed that context. v2 also documents its test `Timeout` as undefined behaviour with
parallelization on, so the suite's hang guard can't be a test-level timeout.

## Decision
- Both test projects move to `xunit.v3`.
- A hung test fails through a test-level `Timeout` plus `TestContext.Current.CancellationToken`,
  passed into gates and waits, instead of a per-call `WaitAsync(HangGuard)`.
- The probabilistic stress nets are marked `Explicit`, and CI runs them in their own step.
- bUnit's `DefaultWaitTimeout` stays routed to the same hang-guard value.

## Considered options
- **Stay on 2.9 and clear the `SynchronizationContext` in every test that advances fake time**:
  rejected. It is a per-test workaround that every new fake-time test must remember, and
  forgetting it gives an intermittent hang, which is the kind of failure this work removes.
- **Disable parallelization to make v2's `Timeout` defined**: rejected. It slows the suite and
  only hides timing problems, it doesn't remove them.

## Consequences
- Test projects become executables (`OutputType Exe`), `IAsyncLifetime` moves to `ValueTask`, and
  `async void` tests fail fast.
- bUnit 1.40 must run under v3. Verify this before the migration commit; if it doesn't, the
  migration includes bUnit 2.x.
- xUnit still has no built-in retry, and the suite must not add one.
