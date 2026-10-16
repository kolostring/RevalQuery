# Plan: make the test suites deterministic

**Goal.** Every test in `RevalQuery.Core.Tests` and `RevalQuery.Blazor.Tests` passes or fails because of the code, never because of how fast the machine ran. No sleeps, no polling until a deadline, no real clock in a pass/fail decision. Where a test cannot be made deterministic, it says so in a documented limitation.

**Basis.** `docs/research/2026-10-16-reliable-tests.md` (patterns P1–P9, TanStack comparison, sources). Every test has an entry in the per-file plans below: what it waits on today, which pattern it falls under, the exact fix, the phase it belongs to, and the risks. Five readers wrote those entries, and the parent session verified the claims they disagreed on (corrections are marked in the files).

| Per-test detail | Files | Tests |
|---|---|---|
| [core-cancellation-queryasync-retry.md](2026-10-16-test-reliability/core-cancellation-queryasync-retry.md) | Cancellation, HandlerCancellation, OrphanedFetch, QueryAsync, QueryAsyncDiscarded, QueryAsyncIntegration, RetryCount, TestUtils | 39 |
| [core-persistence-restore-review.md](2026-10-16-test-reliability/core-persistence-restore-review.md) | RestoreBoundary, Persistence, InvalidateDuringRestore, LoadingDuringRestore, ReviewFindings, ReviewOfA700df3, ReviewOf45161b7, ReviewOf1784595 | 33 |
| [core-polling-gc-toggle-client.md](2026-10-16-test-reliability/core-polling-gc-toggle-client.md) | IssueReproduction, EvictionRace, Polling, EnabledToggle, StaticQuery, Disposal, RevalClient | 45 |
| [core-options-hooks.md](2026-10-16-test-reliability/core-options-hooks.md) | ObserverSetOptions, MutationSetOptions, RevalHooks | 54 |
| [blazor.md](2026-10-16-test-reliability/blazor.md) | RevalRenderer, PrerenderTransfer, ReviewOf1784595, PersistentStateHarness, Fx, .razor fixtures | 30 |

## What the code actually does (verified, the plan rests on it)

1. **Without persistence, everything is synchronous.** `Subscribe`, `Invalidate`, `SetOptions`, `Observe`, `hooks.Query` and `QueryAsync` start the fetch on the caller's thread (`QueryWorker.cs:275-392`). A handler returning a completed task has applied its data, gone Idle and notified before the call returns. `QueryAsync` returns an **already-completed** Task, and the worker is already released (probed on net10.0, first call and refetch). So most `Task.Delay`s and waits in the suite are either wasted (P1a) or vacuous.
2. **With persistence, `RestoreCompleted` is a sufficient signal for state.** The client runs `TryRestore`, then the deferred fetch decision (and a synchronous fetch it starts), and only then completes `RestoreCompleted` (`RevalClient.cs:668-692`, `QueryState.cs:418-447`). It completes **before** the restore's trailing `NotifyChanged`, so tests that count notifications wait on a gate opened from the observer callback instead.
3. **Cancellation has deterministic barriers.** `CancelAsync` waits for the in-flight run to unwind. A cancelled result is dropped before the Idle notification, so "the dropped result never lands" follows synchronously from an awaitable signal.
4. **Only the library's own time is out of reach.** Polling (`QueryWorker.cs:212-219`), retry backoff (`ExponentialBackoffRetryPolicy.cs:37`), GC (`TtlQueryGarbageCollector.cs:42,120,151`) and staleness (`QueryState.cs:293,309`) read the real clock. These need the TimeProvider change (Phase 3).

## Tally

201 entries (one per test method; theories count once).

| | Count | Per file group (cancellation / persistence / polling / options / Blazor) |
|---|---|---|
| No change, or only the hang-guard/vocabulary change | 75 | 9 / 9 / 12 / 28 / 17 |
| Test-only fix (Phase 2) | 112 | 26 / 22 / 28 / 23 / 13 |
| …of which kept as labelled probabilistic stress nets | 3 | 0 / 0 / 2 / 1 / 0 |
| Real fix needs fake time (Phase 3); some get a Phase 2 stopgap | 12 | 2 / 2 / 5 / 3 / 0 |
| Blocked on the staleness bug fix (Phase 1) | 2 | 2 / 0 / 0 / 0 / 0 |
| **Total** | **201** | 39 / 33 / 45 / 54 / 30 |

New tests proposed: two deterministic gated interleavings for the eviction race, `Issue5`'s second ordering, an in-flight teardown test, optionally a single-threaded stampede variant, the polling-race reproduction (L2), the staleness boundary (L1), the backoff schedule, a corrupt-outer-payload test (L6), and a worker-level `NotRun` test (D4).

**Tests that cannot fail today** (rewritten in Phase 2, or in Phase 3 where fake time is needed): `SubscribeThenDispose_PollingStops`, `DiscardedQueryAsyncThenSubscribe_InitialDataFromCache`, `QueryAsync_ConcurrentCalls_SameKey`, both `UsesCachedData` integration tests, `InvalidateAfterQueryAsync_TriggersRefetch` (its wait), `Cancellation_Stops_Retrying_And_Surfaces_The_Failure` (its "surfaces" half), `Discarded_Calls_On_Different_Keys_Are_Independent`, `Hierarchical_Invalidation_Works`, `Resubscribe_Cancels_Eviction`, `RevalClient_Cancel_AbortsFetch`, `Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches` (the ADR cites it as pinning its decision), `A_Restore_Is_Not_Over…` (`Assert.NotEmpty`), `QueryAsync_Does_Not_Return_Data_It_Never_Fetched`, `Resubscribing…Keeps_Polling`, `Stopping_The_Collector…`, `The_Same_Key_Reapplies_Options_Without_Refetching`, `Stale_Transferred_Data_Is_Shown_And_Then_Refetched`, `Disposing_The_Prerender_Transfer_Twice_Is_Harmless`, `The_Transfer_Joins_A_Persistence_Store…`.

## Library defects found

Fix each test-first: a regression test that fails without the fix, in the same commit (`docs/CONVENTIONS.md` #7).

| # | Defect | Where | Fix | Phase |
|---|---|---|---|---|
| L1 | **Staleness off by one.** Age == `StaleTime` counts as fresh, so `StaleTime(0)` is not "always stale" within one clock tick. TanStack: stale when `now >= updatedAt + staleTime` (`query.ts` `isStaleByTime`, `utils.ts` `timeUntilStale`, verified at `ee47993`). Under a frozen fake clock every `StaleTime(0)` refetch test would fail. | `QueryState.cs:293` | `>=` | 1 |
| L2 | **Polling stops under a live observer.** `Subscribe`/`Unsubscribe` raise their events outside the observer lock (`QueryState.cs:503-529`), and `PausePolling` stops polling without re-checking `HasObservers` (`QueryWorker.cs:92`). A subscriber arriving while the last one leaves keeps an unpolled query until its next `SetOptions`. `085e8e6` called this unreachable; it is reachable. | `QueryWorker.cs:92`, `QueryState.cs:503-529` | Re-check `HasObservers` under the worker gate in `PausePolling`, or restart polling when release is refused | 3 (its deterministic test needs fake time) |
| L3 | **`StopAsync`/`DisposeAsync` don't wait after a synchronous `Dispose()`,** contrary to their docs. | `TtlQueryGarbageCollector.cs:68` | Await `_collectionTask` | 1 |
| L4 | **`QueryAsync` gives up under contention** after `MaxRunAttempts = 3` with "could not be run", so the stress nets can fail for a library reason. TanStack's `fetchQuery` has no such bound. | `RevalClient.cs:28, 561-581` | Claim the worker in the lookup (`a-worker-stays-alive-while-a-run-is-claimed-or-in-flight` ADR) | 1 |
| L7 | **GC can cancel an awaited fetch.** `HandleEviction` checks only for observers, so it removes an unobserved query and cancels its fetch while a `QueryAsync` is awaiting it. TanStack's `optionalRemove` requires no observers **and** `fetchStatus` idle. | `RevalClient.cs:640-657` | Eviction refuses a held worker (same ADR) | 1 |
| L5 | **`IMutationState` is dead public API.** Nothing implements it, and its members (`IsError`/`IsSuccess`/`Error`) differ from `MutationState<,>` (`IsException`/`IsResolved`/`Exception`). Present since `3e8a650`. | `Abstractions/Mutation/IMutationState.cs` | Decision D2 | after D2 |
| L6 | (Low) **`PrerenderTransfer.Take` catches `JsonException` only around the inner deserialize.** A corrupt outer payload is "nothing arrived" only because outer layers swallow it. | `PrerenderTransfer.cs:169-182` | Widen the catch | 1 |

Checked and **not** a defect: shortening `StaleTime` on an already-enabled query doesn't refetch. TanStack's `shouldFetchOptionally` also fetches only when the query changed or was disabled (`queryObserver.ts` at `ee47993`).

## Phases

Each numbered step is one commit, with the suite green on net8.0/net9.0/net10.0.

### Phase 0 — TestKit and test hygiene (test-only, no ADR)

1. **Create `RevalQuery.TestKit`** (non-packable, `net8.0;net9.0;net10.0`, references `RevalQuery.Core` only, public API only), referenced by both test projects. Contents:
   - `Gate`: a `TaskCompletionSource` with `RunContinuationsAsynchronously`; `Open()`, `Fail(ex)`, `Task`.
   - `GatedHandler<T>`: per call, records a start (`Started(i)`, `Calls`) and awaits a per-call release (`Release(i, v)`, `ReleaseAll(v)`, `Fail(i, ex)`). It has an **`ignoreToken`** mode, needed by the six cancellation tests whose scenario is a handler that ignores its token, plus `Token(i)` and `Cancelled(i)`. A call released in advance completes synchronously, so the synchronous path stays testable.
   - `GatedPersistence : IQueryPersistence`: `LoadStarted`, `ReleaseLoad(value|null)`, `Saved`, `SaveStarted`. It releases pending loads on dispose, because the library passes the load no cancellation token.
   - `ManualEviction : ICacheEvictionPolicy`: records registrations; `Evict(key)` raises `OnEvictionRequired` synchronously.
   - `Signals.UntilAsync(IObservableQueryState, predicate)` and `Signals.UntilAsync(IMutationState, predicate)`. The mutation overload works once `MutationState<,>` implements `IMutationState` (`mutation-state-implements-imutationstate` ADR); until then it takes `MutationState<TP,TR>`. Both subscribe first, then check, then check inside each `OnChanged`, and complete asynchronously. The doc comment states the ordering rule they rely on: `OnChanged` handlers run in subscription order, so create the wait **before** releasing the gate.
   - `HangGuard`: one constant, used only as `.WaitAsync(HangGuard)`. It turns a hang into a failure and never decides pass or fail, like TanStack's global test timeout.
   - `Counter` (per-test `Interlocked`) and `Discard` (moved unchanged).
2. **Blazor: route bUnit's ceiling through the guard.** Add a `[ModuleInitializer]` setting `TestContextBase.DefaultWaitTimeout = HangGuard`. All 10 `WaitForState`/`WaitForAssertion` calls end in a render and stay as they are; today their 1 s ceiling can decide pass/fail on a slow agent.
3. **Replace `PersistentStateHarness`** with the public `ComponentStatePersistenceManager` (`RestoreStateAsync`/`PersistStateAsync`), a small `IPersistentComponentStateStore`, and the bUnit renderer. Today's harness reflects into private framework members, survives net10 by luck, and has a silent mode where it runs zero callbacks.
4. **Dispose everything a test creates.** `RevalClient`s (the `QueryAsync*`, `PollingTests` and `RevalClientTests` classes leak GC and poll loops for the whole run), DI scopes in the prerender tests, `Fx` and its client.
5. **`EvictionRaceTests.AssertEveryWorkerHasItsState` takes `RevalClient._gate`** while walking the registry. Today it can throw "Collection was modified".
6. **Delete `WaitUntilAsync`, its three private copies** (`ReviewFindingsTests.cs:175`, `IssueReproductionTests.cs:348`, `RevalClientTests.cs:143`), **`WaitForStateAsync` and Blazor `UntilAsync`**, moving each caller to its signal per the per-file entries. Do this file by file in Phase 2; the helpers go in the last commit of Phase 2.

### Phase 1 — library defects fixable now (test first)

7. **L1** `>=` in `IsStale`, with a boundary test. This unblocks `QueryAsync_RefetchesWhenTheDataIsStale` and `QueryAsync_ServesFreshDataHeldBesideAnOlderFailure`.
8. **L3** `StopAsync`/`DisposeAsync` await the loop after `Dispose()`. Rewrite `Stopping_The_Collector…` so it can see the loop end.
9. **L6** widen the `JsonException` catch, with a corrupt-outer-payload test.
9a. **L4 + L7** per the `a-worker-stays-alive-while-a-run-is-claimed-or-in-flight` ADR: claims under the client lock, release and eviction refuse a held worker, and the retry loop and `MaxRunAttempts` are removed. Gated tests with `ManualEviction` release or evict between lookup and run, and during the fetch. They need the Phase 0 TestKit.

### Phase 2 — test-only rewrites, file by file (no ADR)

One commit per file, applying its per-file entries. They share these rules:

- **Synchronous outcome (P1a, and P2 where the decision is synchronous):** delete the sleep or wait and assert straight after the call, as TanStack does (`subscribe(); expect(count).toBe(1)`). This covers all of `EnabledToggleTests`, most of `StaticQueryTests`, and 19 of the 22 `WaitForStateAsync` calls in the options and hooks files.
- **Persistence (P1/P2):** `GatedPersistence` replaces every delay-based store. Hold the load, act, release, await `RestoreCompleted`, assert. Notification-counting tests use an observer-callback gate. "A late restore never overwrites" becomes: hold the load, let the fetch land, release the load, await `RestoreCompleted`, assert. No quiescence signal is needed (it answers the research doc's open question 2).
- **Races (P3):** `GatedHandler` at both ends, released in the order the scenario names, with one test per ordering that matters. The stampede holds the handler until all 16 callers have joined. The eviction race gets deterministic gated tests through `ManualEviction`; the first one fails on the code before `c5562e9`.
- **Cancellation:** `ignoreToken` handlers keep the scenario; `CancelAsync`'s Task is the barrier.
- **Real backoff paid by tests that aren't about backoff** (`HandlerCancellationTests:29` ~6 s, `QueryAsyncTests:31` ~14 s, `A_Failed_Query_Is_Not_Transferred` ~14 s): `Retry(n, _ => TimeSpan.Zero)` or `Retry(0)`. This takes the timer out of the path rather than shortening it; the backoff schedule itself is tested in Phase 3.
- **Weak assertions** (`> 0`, `Contains`, `InRange(1,2)`, `ThrowsAnyAsync<Exception>`) become exact counts, full sequences or specific exception types. Empty or `null` results get positive controls.
- **Vacuous tests** are rewritten to fail on the regression they name, or deleted when they duplicate another (`A_Transfer_Without_An_Invalidation_Is_Reused`).
- **Stress nets** (`Issue4_Concurrent_QueryAsync`, the `EvictionRace` storm, `ObserverSetOptionsRaceTests`) stay in a non-parallel collection and are labelled as probabilistic regression nets in their doc comment. Their inner waits fail loudly, and they no longer block thread-pool threads on a `Barrier`. Until D3 is decided, their "could not be run" failure is a known library cause (L4), not a test flake.
- **Statics and clock identity (P7):** per-test `Counter`s; `Interlocked` counters replace `DateTime.UtcNow.Ticks`.
- **Fixes that stay pending:** `QueryAsync_Does_Not_Return_Data_It_Never_Fetched` gets the worker-level `NotRun` test and restores the limitation note that `a449568` deleted (D4).

Last commit of the phase: delete the old helpers (step 6) and add the **mechanical check** (step 12) with its current allow-list.

### Phase 3 — inject `TimeProvider` (needs an ADR first)

10. **Write the ADR** in `cfd:adr` format, recommending the research doc's option (c):
    - `RevalQueryOptions.TimeProvider`, defaulting to `TimeProvider.System` and **public**, as TanStack's `timeoutManager` is. It is BCL on net8, so Core takes no new dependency.
    - The poll and GC timers are created **synchronously** with `TimeProvider.CreateTimer`, under the gate where `StartPolling`/`EnsureStarted` arm them today, and kept in fields. `Task.Delay` inside `Task.Run` can't be driven, because the test can't know when the delay exists. `PeriodicTimer` handles poorly under `Advance` (dotnet/extensions#6810).
    - Retry uses `Task.Delay(delay, timeProvider, ct)`.
    - Staleness and GC expiry use `GetUtcNow()`.
    - Threading: timer callbacks run on a timer thread in production (as `Task.Delay` continuations do today) and on the `Advance` caller in tests. The `supported-hosting-models-and-the-thread-safety-contract` ADR already accepts timer-thread callbacks into the client.
    - Per-key GC timers (TanStack `removable.ts`) are **out of scope**. They would remove the public `GcInterval` option, so they get their own ADR if wanted.
11. **Implement it, then move the Phase 3 tests to `FakeTimeProvider`.** That covers: polling (`PollingTests`, `ObserverSetOptionsTests.cs:88`, `Switching_To_A_Key_Held_Only_By_Disabled_Observers_Starts_Polling`), GC (`Issue2_BackgroundEviction`), staleness (`ServesFreshDataFromCache…`, `JudgesStalenessByTheOptions…`, `The_Same_Key_Reapplies_Options_Without_Refetching`, `A_Change_Raised_While_Reading…`, the `ReviewFindings` freshness tests), and new tests for the backoff schedule and **L2** (`Resubscribing_From_Inside_The_Last_Unsubscribe_Keeps_Polling`, then the fix). Advance by exactly the scheduled amount, as TanStack's tests do. Until xUnit v3 (Phase 4), tests that `Advance` clear the `SynchronizationContext` first, per the `FakeTimeProvider` README.

### Phase 4 — xUnit v3

Move both projects to `xunit.v3`. This removes the `AsyncTestSyncContext` that `FakeTimeProvider`'s README blames for hangs, so the sync-context workaround goes too. `HangGuard` becomes a test-level `Timeout` plus `TestContext.Current.CancellationToken`, and the stress nets become `Explicit` or a separate collection. Before committing, check that bUnit 1.40 works under v3 (unverified).

### Guardrail

12. **A CI check** that fails the build when `RevalQuery.*.Tests` or `RevalQuery.TestKit` contain `Task.Delay(`, `Thread.Sleep(`, `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Stopwatch` or `WaitUntil`. `HangGuard` is the one allowed timeout. Its allow-list holds only the Phase 3 tests and shrinks to empty when Phase 3 lands. Record the rule in `docs/CONVENTIONS.md`.

## Decisions for the maintainer

| # | Decision | Recommendation |
|---|---|---|
| D1 | TimeProvider ADR as described in Phase 3 | **Decided:** public option, timers armed synchronously. See `docs/adr/2026-10-16-library-time-comes-from-an-injected-timeprovider.md` |
| D2 | `IMutationState` (L5) | **Decided:** implement it, with the query side's names. See `docs/adr/2026-10-16-mutation-state-implements-imutationstate.md` |
| D3 | `MaxRunAttempts` give-up (L4) | **Decided:** claim the worker in the lookup, so `NotRun` can't happen and the bound goes. See `docs/adr/2026-10-16-a-worker-stays-alive-while-a-run-is-claimed-or-in-flight.md`, which also covers L7 |
| D4 | `QueryAsync_Does_Not_Return_Data_It_Never_Fetched` | **Decided:** a worker-level test plus a documented limitation (`085e8e6` rejected the seam) |
| D5 | xUnit v3 (Phase 4) | **Decided:** yes, as the last phase. See `docs/adr/2026-10-16-tests-run-on-xunit-v3.md` |

## Order and dependencies

Phase 0 → Phase 1 → Phase 2 (files in any order) → D1 → Phase 3 → Phase 4. D2 and D3 can be decided at any time; their fixes slot in wherever they're decided. The guardrail lands at the end of Phase 2 and tightens at the end of Phase 3.
