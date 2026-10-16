# G2: restore, persistence and review-finding tests

Scope: `RevalQuery.Core.Tests/` RestoreBoundaryTests, PersistenceTests, InvalidateDuringRestoreTests, LoadingDuringRestoreTests, ReviewFindingsTests, ReviewOfA700df3Tests, ReviewOf45161b7Tests, ReviewOf1784595Tests. 33 tests.

## Library facts these fixes rely on (verified by reading, not run)

- **F1. No persistence + synchronous handler means the whole fetch is synchronous.** `Subscribe` → `RunIfStale` → `TryDeferUntilRestored` returns false (`QueryState.cs:402`, no restore) → `RunIfStaleNow` → `RunAsync` → `RunCoreAsync` → `RunFetchAsync` (`QueryWorker.cs:248-301, 335-392`). The retry policy's first attempt awaits nothing (`ExponentialBackoffRetryPolicy.cs:30-43`). `ApplyFetched` therefore runs before `Subscribe`/`Invalidate` returns. The same holds for `QueryAsync`: the completion is set inside `RunCoreAsync`'s `finally` before `RunAsync` returns, so the returned task has already completed.
- **F2. With persistence, the restore opens synchronously and the load runs on `Task.Run`.** `BeginRestore()` runs under the client lock before `Task.Run(LoadPersistedAsync)` (`RevalClient.cs:117-121`). Subscribe's `RunIfStale` hands its decision to the restore whenever the restore has not started ending. If the load has already finished, Subscribe decides for itself. With today's delay-based fakes, which of the two happens is up to the scheduler. A gated load pins the deferred path.
- **F3. Order inside `LoadPersistedAsync` (`RevalClient.cs:668-692`):** `LoadAsync` → `TryRestore` → `CompleteRestore()` (runs the deferred decision, then `completion.SetResult()`, `QueryState.cs:418-445`) → `NotifyChangedSafely`. So:
  - awaiting `RestoreCompleted` happens after `TryRestore` and after the deferred decision. A fetch that the decision starts has already set `Fetching` and raised its first notification. A synchronous handler has already landed.
  - awaiting `RestoreCompleted` does **not** happen after the restore's own trailing `NotifyChanged`. Tests that assert on notifications (frames, counters) need a notification-driven signal, not `RestoreCompleted`.
- **F4. `QueryAsync` never defers to a restore.** It calls `worker.RunAsync()` directly (`RevalClient.cs:239-245, 569`), so its fetch runs alongside an open restore. `TryRestore` then declines on `_hasSettled` (`QueryState.cs:352`), which both `ApplyFetched` and `ApplyFailed` set.
- **F5. The load is called with no cancellation token** (`RevalClient.cs:676`). If a `GatedPersistence` load is never released, it pends forever after the client is disposed. Every test must release its load, or the TestKit fake must release on dispose.

## RevalQuery.Core.Tests/RestoreBoundaryTests.cs

Fixtures and statics:
- `Store(entry, delay)` (`:14-24`) uses `Task.Delay(delay)` as latency. Replace it with `GatedPersistence`. No statics.
- `Watch` (`:28-43`): the callback reads `observer!.Query`, and `observer` is null until `Subscribe` returns. A notification raised inside `Subscribe` therefore throws a NullReferenceException, `NotifyChangedSafely` swallows it, and the frame is lost. That can happen today when the 150 ms load wins the race and Subscribe fetches inline. Change `Watch` to take the `QueryState` from `client.GetOrCreateQuery(options)` before subscribing, record frames from it, and expose:
  - `Snapshot()`: a copy of the frames taken under the lock. The assertions at `:62-63, :90, :120` read the list without the lock today.
  - `FirstFrameAfterRestore`: a `Gate` opened by the callback the first time it appends a frame with `!IsRestoring`.

### `A_Query_With_Nothing_Stored_Never_Reports_A_Moment_With_No_Work_In_Progress` (`RestoreBoundaryTests.cs:46`)
- **Today:** a 150 ms load and a 150 ms handler (`:18, :54`), then a fixed `Task.Delay(800)` (`:60`). If the work overruns, `Data` fails. Worse, if the trailing restore notification lands after the sleep, the frame that would expose a regression is never seen, and the test passes falsely.
- **Pattern:** P1 + P3.
- **Fix:**
  1. Set up `GatedPersistence` (nothing stored) and a `GatedHandler<string>`. Call `Watch`, then `await persistence.LoadStarted.WaitAsync(HangGuard)`.
  2. `Assert.True(state.IsRestoring && state.IsLoading)` and `Assert.Equal(0, handler.Calls)`, because the decision was deferred.
  3. `persistence.ReleaseLoad(null)`, then `await watch.FirstFrameAfterRestore.WaitAsync(HangGuard)`. Under today's code this is the trailing notification, raised while the gated fetch is still running. If the boundary regressed (restore ends, then the worker decides), this same notification is the blank frame. Waiting for it is what makes the negative assertion sound.
  4. `await handler.Started(0).WaitAsync(HangGuard)`, then `Assert.True(state.IsFetching)`.
  5. `handler.Release(0, "from-network")`, then `await Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle)`.
  6. On `Snapshot()`: `DoesNotContain(f => f.IsPending && !f.IsLoading)` and `Contains(f => f.IsFetching)`. Then `Assert.Equal("from-network", state.Data)`.
- **Phase:** A.
- **Risk:** The test depends on the restore's trailing notification. Step 3 has to wait on that notification, not on `RestoreCompleted` (F3).

### `A_Restore_Is_Not_Over_Until_The_Fetch_That_Follows_It_Has_Started` (`:68`)
- **Today:** a 150 ms load and a 300 ms handler. `RestoreCompleted` is awaited with a 2 s guard (`:83`). `IsFetching` is asserted on the assumption that the 300 ms handler has not finished yet (P3). Then `Task.Delay(500)` (`:88`) before `Data`. A starved scheduler can make the load finish before `RunIfStale`, and the test then passes without exercising the deferred path at all (F2).
- **Pattern:** P3 + P1.
- **Fix:**
  1. Set up `GatedPersistence` (nothing stored) and a `GatedHandler`. `state = GetOrCreateQuery`, then `Watch`.
  2. `Assert.True(state.IsRestoring)` and `Assert.Equal(0, handler.Calls)`.
  3. `ReleaseLoad(null)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  4. Assert synchronously: `state.IsFetching`, `!state.IsRestoring`, `handler.Started(0).IsCompleted` (F3: the decision ran before `SetResult`), and on `Snapshot()`, `Contains(f => f.IsFetching && f.IsRestoring)`. The last assertion is the ADR property itself: fetch status was set while the restore was still open. It replaces the vacuous `Assert.NotEmpty(frames)`.
  5. `handler.Release(0, "from-network")`, then `await Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle)`, then `Assert.Equal("from-network", state.Data)`.
- **Phase:** A.
- **Risk:** `NotEmpty(frames)` (`:90`) can never fail. Replace it, don't keep it.

### `Stored_Data_That_Is_Still_Fresh_Ends_The_Restore_With_No_Fetch_At_All` (`:94`)
- **Today:** a 150 ms load with a fresh entry, then `Task.Delay(600)` (`:115`). It then asserts 0 handler calls (negative, P2), `from-disk`, and no blank frame.
- **Pattern:** P2 + P1.
- **Fix:**
  1. Use `GatedPersistence(fresh entry with QueryFreshness(DateTimeOffset.UtcNow))` and a `Counter` handler with `StaleTime` 10 min. Call `Watch` and `await LoadStarted`.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  3. Assert `handler calls == 0`. This is deterministic: the only decision point was the deferred one, it has run (F3), and nothing else can trigger a fetch (no poll, no invalidate).
  4. Assert `Data == "from-disk"` and `!IsRestoring`.
  5. `await watch.FirstFrameAfterRestore.WaitAsync(HangGuard)`, then `DoesNotContain(f => f.IsPending && !f.IsLoading)`.
- **Phase:** A.
- **Risk:** Freshness still reads the real clock: stored `UtcNow` against a 10 minute `StaleTime`. That is not a flake mode in practice. Under phase B, store `fake.GetUtcNow()` and never advance, which makes it exact.

### `A_Subscriber_Arriving_After_The_Restore_Ended_Still_Fetches` (`:124`)
- **Today:** a 100 ms load. `RestoreCompleted` is awaited with a 2 s guard (`:133`). Then `Subscribe`, `Task.Delay(300)` (`:138`), and an assert on `Data`.
- **Pattern:** P1a. The research doc lists `:138` as P1, but it is synchronous: the restore is over, so the decision is not deferred, and F1 applies.
- **Fix:**
  1. `GatedPersistence` (nothing stored), a `Counter` handler, `state = GetOrCreateQuery`.
  2. `ReleaseLoad(null)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`, then `Assert.False(state.IsRestoring)`.
  3. `observer = Subscribe(...)`. Immediately after, assert `Data == "from-network"` and `calls == 1`.
- **Phase:** A.
- **Risk:** `GatedPersistence.SaveAsync` must return a completed `ValueTask`. If it doesn't, the fetch's completion pends, although `Data` is still set.

### `A_Restore_That_Is_Already_Ending_Refuses_To_Take_Over_A_Decision` (`:144`)
- **Today:** deterministic, no change. Fully synchronous on a bare `QueryState`. It fails if `_restoreEnding` is removed, because the inner `TryDeferUntilRestored` then returns true.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `A_Query_With_No_Persistence_Is_Never_Restoring` (`:167`)
- **Today:** synchronous asserts, then `Task.Delay(200)` (`:180`) before the assert on `Data`.
- **Pattern:** P1a.
- **Fix:** Delete the sleep and assert `observer.Query.Data == "from-network"` straight after `Subscribe`.
- **Phase:** A.
- **Risk:** This duplicates `LoadingDuringRestoreTests.A_Query_Without_Persistence_Is_Never_Restoring`. Consider keeping only one.

## RevalQuery.Core.Tests/PersistenceTests.cs

Fixtures and statics:
- `InMemoryPersistence` (`:122-139`) is synchronous and fine for tests 1 and 6. Its `LoadAsync` still runs on `Task.Run` alongside the test. That is harmless here, because `TryRestore` declines once the fetch has settled.
- `SlowPersistence` (`:141-169`) is already gated, but `AllowLoad` ends with `Task.Delay(50)` (`:150`) to "let `TryRestore` run". Replace it with `GatedPersistence` plus `RestoreCompleted`. Its 2 s `_loadReturned` guard (`:149`) becomes `HangGuard`.
- `ThrowingPersistence` (`:171-178`) throws synchronously. Keep it.
- No statics.

### `SuccessfulFetch_Is_Saved_With_Its_Timestamp` (`:17`)
- **Today:** deterministic, no change. The fetch and the synchronous save complete inside `QueryAsync` (F1, `QueryWorker.cs:389, 405-416`).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `PersistedData_Is_Restored_Before_The_Fetch_Lands` (`:31`)
- **Today:** `InMemoryPersistence` with a synchronous load, so the deferred and non-deferred paths are a scheduler coin flip (F2). It waits with `WaitForStateAsync(IsResolved)` (`:48`), which has a lost-wakeup race: check, then subscribe, so a change in between means a 2 s timeout. Then `WaitForStateAsync(IsIdle && LastUpdatedAt > fetchedAt)` (`:54`).
- **Pattern:** P5 (+ F2 nondeterminism).
- **Fix:**
  1. Use `GatedPersistence(entry fetched 3 h ago)` and a `GatedHandler` in place of the `SemaphoreSlim` gate. `Subscribe`, then `await LoadStarted`.
  2. `Assert.True(state.IsRestoring)` and `Assert.Equal(0, handler.Calls)`.
  3. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  4. Assert synchronously: `Data == "from-disk"`, `LastUpdatedAt == fetchedAt`, `IsResolved`, `IsFetching`, `handler.Started(0).IsCompleted`.
  5. `handler.Release(0, "from-network")`, then `await Signals.UntilAsync(state, s => s.IsIdle && s.Data == "from-network")`, then `Assert.True(state.LastUpdatedAt > fetchedAt)`.
- **Phase:** A.
- **Risk:** Staleness rests on "UtcNow minus 3 h > StaleTime 0". The margin is huge. B makes it exact.

### `A_Fetch_That_Already_Landed_Is_Not_Overwritten_By_The_Load` (`:59`)
- **Today:** the fetch lands, then `AllowLoad()` opens the gate, waits for `LoadAsync` to return, then sleeps 50 ms (`:150`) hoping `TryRestore` has run. If `TryRestore` is late, the test passes falsely (a negative assertion).
- **Pattern:** P2.
- **Fix:**
  1. `GatedPersistence(stale entry)`, then `await ClientFetch(...)`. Then `landedAt = state.LastUpdatedAt` and `Assert.True(state.IsRestoring)`. The load is still held, which proves the restore is genuinely late.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`. `TryRestore` has now run (F3).
  3. Assert `Data == "from-network"` and `LastUpdatedAt == landedAt`.
- **Phase:** A.
- **Risk:** This duplicates `ReviewFindingsTests.A_Late_Restore_Never_Overwrites_A_Landed_Fetch`. `GatedPersistence.SaveAsync` must complete synchronously, or `ClientFetch` hangs.

### `A_Failing_Adapter_Leaves_The_Query_Working` (`:75`)
- **Today:** deterministic, no change. The adapter throws synchronously, both throws are caught (`RevalClient.cs:678-681`, `QueryWorker.cs:413`), and `QueryAsync` is synchronous.
- **Pattern:** ok.
- **Fix:** Optional strengthening: `await state.RestoreCompleted.WaitAsync(HangGuard); Assert.False(state.IsRestoring)`. This proves a throwing load still ends the restore (the `finally` at `RevalClient.cs:687`), which the test does not check today because `QueryAsync` bypasses the restore (F4).
- **Phase:** none (the optional addition would be A).
- **Risk:** none.

### `Persistence_Is_Optional_In_DI` (`:86`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `A_Registered_Adapter_Is_Picked_Up_By_The_Client` (`:95`)
- **Today:** deterministic, no change. The synchronous save completes inside `QueryAsync`.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

## RevalQuery.Core.Tests/InvalidateDuringRestoreTests.cs

Fixtures and statics: the local `GatedPersistence` (`:13-27`, a `SemaphoreSlim` plus a `LoadStarted` TCS) is already gated. Replace it with the TestKit `GatedPersistence`. The 2 s `WaitAsync` guards (`:49, :54, :84`) become `HangGuard`. No statics.

### `An_Invalidation_During_A_Restore_Survives_When_Nothing_Can_Fetch_Yet` (`:30`)
- **Today:** deterministic up to `:58`. After `Subscribe`, it uses the silent `TestUtils.WaitUntilAsync(calls == 1)` (`:60`). The fetch is in fact synchronous: the restore is over, the query is invalidated so it is stale, and F1 applies.
- **Pattern:** P1a + P5.
- **Fix:**
  1. Keep `:47-56`: `GetOrCreateQuery`, `await LoadStarted`, `Invalidate`, `ReleaseLoad(entry)`, `await RestoreCompleted`, `calls == 0`. Add `Assert.True(state.IsInvalidated)`. That proves the flag survived `TryRestore`'s `|=` (`QueryState.cs:359`).
  2. `Subscribe`, then immediately assert `calls == 1` and `Data == "from-network"`. Delete `WaitUntilAsync`.
- **Phase:** A.
- **Risk:** This is the test that actually pins the `_isInvalidated` mechanism. The stored entry is fresh (10 min `StaleTime`), so only the flag can make the query stale.

### `Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches` (`:65`)
- **Today:** `Task.Delay(400)` (`:90`), then `calls > 0`. Both the invalidation fetch and the restore's decision are synchronous, so the sleep is unneeded.
- **Pattern:** P1 (it is really P1a).
- **Fix:**
  1. `Subscribe` (the decision is deferred), then `await LoadStarted`.
  2. `Invalidate("gated")`. Immediately after, while the load is still held, assert `state.IsRestoring`, `calls == 1`, `Data == "from-network"`. This is the ADR's claim: `CanFetch` is true throughout the restore, so the invalidation fetches at once (`QueryWorker.cs:232-263`, F1).
  3. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  4. Assert `Data == "from-network"` (`TryRestore` declined on `_hasSettled`) and `calls == 1` exactly. The deferred decision saw fresh data and did not fetch again, and it has already run (F3).
- **Phase:** A.
- **Risk:** **As written, the test cannot catch the regression its ADR says it pins.** If a restore reported `Fetching`, the invalidation would be dropped mid-restore. But `_isInvalidated` would survive `TryRestore`, and the deferred decision would refetch after the restore, so `calls > 0` would still pass after 400 ms. Step 2's synchronous assertion while `IsRestoring` is what restores the guard. `calls == 1` is a strengthening. If the maintainer considers a second fetch acceptable, keep `>= 1` there, but step 2 must stay.

## RevalQuery.Core.Tests/LoadingDuringRestoreTests.cs

Fixtures and statics: the local `GatedPersistence` (`:14-28`) becomes the TestKit `GatedPersistence`. The 2 s guards (`:50, :80`) become `HangGuard`. No statics.

### `A_Query_Waiting_On_A_Restore_Is_Loading_But_Not_Fetching` (`:31`)
- **Today:** the asserts while the load is held (`:52-57`) are deterministic. After the release it waits with `WaitForStateAsync(!IsRestoring)` (`:62`), which is P5: a lost wakeup if the trailing notification lands between the check and the subscribe. `handlerGate` (`:38`) is dead code: the entry is fresh, so the handler never runs. It also lacks `RunContinuationsAsynchronously`.
- **Pattern:** P5.
- **Fix:**
  1. Replace `handlerGate` with a `GatedHandler`. Keep `:47-57`.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`. Get `state` via `client.FindQuery<string>("restoring")`, since `RestoreCompleted` is on the concrete type.
  3. Assert `!IsRestoring`, `!IsLoading`, `IsResolved`, `Data == "from-disk"`, `handler.Calls == 0`. The last one makes the "not fetching" claim real.
- **Phase:** A.
- **Risk:** none.

### `A_Restore_That_Finds_Nothing_Stops_Loading_And_Notifies` (`:67`)
- **Today:** `WaitForStateAsync(!IsLoading)` (`:85`) returns as soon as the state flips, which can be before the trailing notification (F3). The silent `WaitUntilAsync(changes > 0)` (`:89`) then papers over that. If no notification ever arrived, it returns after 3 s and the assert fails with no explanation.
- **Pattern:** P5.
- **Fix:**
  1. The observer callback opens a `Gate notified` and records `(IsLoading, IsRestoring, IsPending)` at the moment of the call. Nothing notifies before the release: the query is disabled, the decision is deferred, and no fetch runs.
  2. `await LoadStarted`, then `Assert.True(state.IsLoading)`.
  3. `ReleaseLoad(null)`, then `await notified.Task.WaitAsync(HangGuard)`.
  4. Assert that the recorded snapshot is `!IsLoading && !IsRestoring && IsPending`. This proves the notification itself carried the post-restore state. Then assert the same on `state`.
- **Phase:** A.
- **Risk:** It relies on no earlier notification. That holds because the query is disabled (`QueryWorker.cs:133-137`, then `CanFetch` is false in the deferred decision).

### `A_Query_Without_Persistence_Is_Never_Restoring` (`:94`)
- **Today:** `WaitForStateAsync(IsResolved)` (`:103`) always returns on its first check (F1).
- **Pattern:** P1a (hidden).
- **Fix:** Assert `!IsRestoring`, `IsResolved` and `!IsLoading` straight after `Subscribe`.
- **Phase:** A.
- **Risk:** This duplicates `RestoreBoundaryTests.A_Query_With_No_Persistence_Is_Never_Restoring`.

## RevalQuery.Core.Tests/ReviewFindingsTests.cs

Fixtures and statics:
- The private `WaitUntil` (`:175-179`, polls every 5 ms, returns silently) is deleted.
- `StubPersistence(stored, loadDelayMs)` (`:181-191`) becomes `GatedPersistence`.
- No statics.
- Line references in the research doc for this file are 1-3 lines off: `UtcNow.AddHours(-3)` is at `:114/:136`, and the `LastUpdatedAt` assert is at `:129`.

### `An_Observer_That_Throws_Does_Not_Wedge_The_Query` (`:18`)
- **Today:** `WaitUntil(calls == 1)` (`:31`) and `WaitUntil(calls == 2)` (`:34`). Both fetches are synchronous (F1). The throwing observer is absorbed by `NotifyChangedSafely` (`QueryWorker.cs:394-403`).
- **Pattern:** P1a + P5.
- **Fix:** After `Subscribe`, assert `calls == 1` and `IsIdle`. After `Invalidate`, assert `calls == 2`, `IsIdle` and `IsResolved`.
- **Phase:** A.
- **Risk:** none. A wedge leaves `Fetching` set, so the second assertion fails synchronously.

### `QueryAsync_Does_Not_Return_Data_It_Never_Fetched` (`:41`)
- **Today:** the handler runs `Task.Delay(30, ct)` and the test sleeps `Task.Delay(5)` (`:52`) so the second call lands "mid-fetch". The sleep is unneeded: the handler is already in flight when `QueryAsync` returns (F1's synchronous prefix). If the 5 ms overruns 30 ms, the second call misses the fetch and the test passes without testing anything.
- **Pattern:** P3.
- **Fix:**
  1. With a `GatedHandler`: `fetch = client.QueryAsync(options)`, then `Assert.Equal(1, handler.Calls)`.
  2. `second = client.QueryAsync(options)`, then `Assert.Equal(1, handler.Calls)`. This proves the second call joined the running fetch (`QueryWorker.cs:285-287`).
  3. `handler.Release(0, "value")`, then `Assert.Equal("value", await fetch.WaitAsync(HangGuard))` and `Assert.Equal("value", await second.WaitAsync(HangGuard))`. The second assert replaces `Discard`.
- **Phase:** A for the join path. **NEEDS-SIGNAL** for the invariant the name refers to (see Risk).
- **Risk:** **The test cannot fail for the bug it is named after.** Commit `085e8e6` recorded this: the guarded path is `RunAndReleaseAsync` retrying on `FetchOutcome.NotRun` when the worker is released between `PrepareRun` and `RunAsync` (`RevalClient.cs:561-576`). No user code runs in that window, so no test can open it. That note was deleted in `a449568`. Options:
  - (a) Add a worker-level test on the public type. Construct a `QueryWorker`, `Dispose()` it, and assert `await RunAsync() == FetchOutcome.NotRun`. This is phase A and covers half of the invariant.
  - (b) A test-only seam between `PrepareRun` and `RunAsync`. That is the NEEDS-SIGNAL item. The maintainer previously rejected a seam (`085e8e6`).
  - (c) Restore the documented limitation as a comment.

  Recommend (a) plus (c), unless (b) is accepted.

### `Resubscribing_While_The_Previous_Observer_Leaves_Keeps_Polling` (`:59`)
- **Today:** a 30 ms `RefetchInterval`. `WaitUntil(calls >= before + 3)` (`:81`) is silent with a 3 s timeout, and the assertion repeats the predicate it waited on.
- **Pattern:** P4 + P5.
- **Fix (B):**
  1. Set up a `FakeTimeProvider` via `RevalQueryOptions.TimeProvider`, with `SynchronizationContext.SetSynchronizationContext(null)` under xUnit v2.
  2. Run the same 20-round churn, then `observer = Subscribe`, then `before = calls`.
  3. Three times: `fake.Advance(30 ms)`, then `Assert.Equal(before + i, calls)`. Exact equality also catches a leaked second poller from the churn.
  4. `observer.Dispose()`, `fake.Advance(30 ms)`, then `Assert.Equal(before + 3, calls)`.

  **Add a new test**, `Resubscribing_From_Inside_The_Last_Unsubscribe_Keeps_Polling` (B), that opens the interleaving `085e8e6` called unreachable, through the public `QueryState.OnLastSubscriberRemoved` event:
  1. Call `GetOrCreateQuery`, then attach a test handler to `OnLastSubscriberRemoved` before the first `Subscribe`. The invocation order becomes [client lifecycle, test, worker `PausePolling`].
  2. Subscribe A with a `GatedHandler` so a fetch is in flight. Then `A.Dispose()`. The client's handler calls `DisposeWorker`, `TryRelease` returns false because a fetch is in flight, and the worker stays on the node. The test handler subscribes B, which gets the same worker, and `StartPolling` is a no-op because polling is still active. Then the worker's `PausePolling` cancels polling.
  3. `handler.Release(0, x)`, then `before = calls`, `fake.Advance(30 ms)`, then `Assert.Equal(before + 1, calls)`.

  **I expect this to fail today** (see Findings).
- **Phase:** B.
- **Risk:** Under fake time, `StaleTime` 0 with a frozen clock makes resubscribes during the churn not stale, so `before` changes. That is harmless because the asserts are relative.

### `Fresh_Persisted_Data_Suppresses_The_Fetch` (`:88`)
- **Today:** `WaitUntil(IsResolved)` (`:103`, silent) plus `Task.Delay(100)` (`:104`), then `calls == 0` (a negative assertion).
- **Pattern:** P2 + P5.
- **Fix:**
  1. `GatedPersistence(fresh entry)`, `Subscribe`, `await LoadStarted`. Then `Assert.Equal(0, calls)`: the decision was deferred.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  3. Assert `calls == 0`, `Data == "from-disk"`, `IsIdle`. The deferred decision has run (F3), and nothing else can trigger a fetch.
- **Phase:** A.
- **Risk:**
  - `085e8e6` says this catches "RunIfStale deciding without awaiting the load". It still does: with the load held, a non-deferring `RunIfStale` fetches, because the query is pending and therefore stale.
  - This duplicates `RestoreBoundaryTests.Stored_Data_That_Is_Still_Fresh…`.
  - Optional second ordering: `GetOrCreateQuery`, `ReleaseLoad`, `await RestoreCompleted`, then `Subscribe` and assert `calls == 0`. That exercises the non-deferred path deterministically.

### `Stale_Persisted_Data_Still_Refetches` (`:111`)
- **Today:** `WaitUntil(Data == "from-network")` (`:123`, silent) and `Task.Delay(150)` (`:125`, "nothing in flight could change it"). Then `LastUpdatedAt > UtcNow.AddMinutes(-1)` (`:129`), which reads the real clock.
- **Pattern:** P5 + P2 + P4.
- **Fix:**
  1. `GatedPersistence(entry at storedAt = UtcNow - 3h)` and a `GatedHandler`. `Subscribe`, then `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  2. Assert `Data == "from-disk"` and `LastUpdatedAt == storedAt`. This is the invariant `085e8e6` says the test is really about: `TryRestore` keeps the original timestamp. It is now asserted directly instead of being inferred.
  3. Assert `IsFetching` and `handler.Started(0).IsCompleted`.
  4. `handler.Release(0, "from-network")`, then `await Signals.UntilAsync(state, s => s.IsIdle && s.Data == "from-network")`, then `Assert.True(state.LastUpdatedAt > storedAt)`.
- **Phase:** A. B makes the last assertion exact: `== fake.GetUtcNow()`.
- **Risk:** The 150 ms "nothing changes later" sleep is unnecessary because the restore is fully over at `RestoreCompleted`.

### `A_Late_Restore_Never_Overwrites_A_Landed_Fetch` (`:133`)
- **Today:** a 120 ms load, `QueryAsync` lands, then `Task.Delay(250)` (`:145`). If the load runs past 250 ms, the test passes falsely.
- **Pattern:** P2.
- **Fix:**
  1. `GatedPersistence(stale entry)`, `await client.QueryAsync(options)`. Then `landedAt = state.LastUpdatedAt` and `Assert.True(state.IsRestoring)`.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  3. Assert `Data == "from-network"` and `LastUpdatedAt == landedAt`.
- **Phase:** A.
- **Risk:** This duplicates `PersistenceTests.A_Fetch_That_Already_Landed…`. Keep one.

### `An_Overfull_Death_Row_Evicts_Rather_Than_Forgetting` (`:153`)
- **Today:** deterministic, no change. `EvictOldestEntries` runs synchronously inside `RegisterForEviction` (`TtlQueryGarbageCollector.cs:47`). The background loop has a 1 h interval and never fires.
- **Pattern:** ok.
- **Fix:** none. Under B, it could pass a `FakeTimeProvider` so the loop's timer is never real.
- **Phase:** none.
- **Risk:** none.

## RevalQuery.Core.Tests/ReviewOfA700df3Tests.cs

Fixtures and statics: `SlowPersistence(stored, loadDelayMs)` (`:97-109`) becomes `GatedPersistence`. No statics.

### `A_Cancelled_Fetch_Does_Not_Return_Data_It_Never_Fetched` (`:13`)
- **Today:** the handler `Task.Delay(5000, ct)` keeps the fetch alive by a timer. `started` (`:17`) lacks `RunContinuationsAsynchronously`. That is harmless, since it completes before it is awaited.
- **Pattern:** P3 (a mild one: the timer only has to outlast the cancel).
- **Fix:**
  1. Use a `GatedHandler` that is never released and honours the token. `fetch = QueryAsync`, then `Assert.Equal(1, handler.Calls)`.
  2. `await client.CancelAsync("cancelled").WaitAsync(HangGuard)`.
  3. `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(HangGuard))`.
- **Phase:** A.
- **Risk:** Do not assert `fetch.IsCompleted` right after `CancelAsync`. `QueryAsync`'s continuation runs asynchronously (`QueryWorker.cs:292`).

### `A_Restore_Does_Not_Clear_A_Failed_Query` (`:34`)
- **Today:** a 150 ms load, the fetch fails synchronously, then `Task.Delay(250)` (`:49`) before the asserts. This is negative: a late load would be missed.
- **Pattern:** P2.
- **Fix:**
  1. `GatedPersistence(fresh entry)`. `QueryAsync` throws. Then assert `IsException` and `IsRestoring`: the restore is genuinely still pending.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  3. Assert `IsException`, `Exception != null`, `!IsResolved`, `Data == null`.
- **Phase:** A.
- **Risk:** It fails if `ApplyFailed` stops setting `_hasSettled`. Good.

### `A_Successful_Fetch_Clears_An_Earlier_Failure` (`:57`)
- **Today:** deterministic, no change. No persistence and synchronous handlers (F1).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `A_Failed_Fetch_Still_Throws_When_A_Restore_Lands_During_It` (`:80`)
- **Today:** a 30 ms load must land inside an 80 ms failing handler (`:87`). If the load is late, `TryRestore` declines and the test passes without the interleaving ever happening.
- **Pattern:** P3.
- **Fix:**
  1. `GatedPersistence(fresh entry)` and a `GatedHandler`. `fetch = QueryAsync`, then `Assert.Equal(1, handler.Calls)`.
  2. `ReleaseLoad(entry)`, then `await state.RestoreCompleted.WaitAsync(HangGuard)`.
  3. Assert `IsResolved` and `Data == "from-disk"`. That proves the restore really landed mid-fetch.
  4. `handler.Fail(0, new InvalidOperationException("boom"))`, then `await Assert.ThrowsAsync<InvalidOperationException>(() => fetch.WaitAsync(HangGuard))`, then `Assert.True(state.IsException)`.
- **Phase:** A.
- **Risk:** Step 3 pins "a restore adopts over an unsettled fetch", which today is behaviour (`QueryState.cs:352`). It is needed as proof of the interleaving.

## RevalQuery.Core.Tests/ReviewOf45161b7Tests.cs

Fixtures and statics: none.

### `A_Static_QueryAsync_Does_Not_Freeze_The_Key_For_Later_Subscribers` (`:11`)
- **Today:** silent `WaitUntilAsync(calls == 2)` (`:40`). The invalidation fetch is synchronous (F1).
- **Pattern:** P1a + P5.
- **Fix:** After `Subscribe`, assert `calls == 1` (new: fresh within 10 min, so no fetch on mount). Then `Invalidate`, then immediately `Assert.Equal(2, calls)`.
- **Phase:** A.
- **Risk:** The real clock against a 10 minute margin. Fine. B makes it exact.

### `Invalidating_A_Static_Query_That_Never_Produced_Data_Refetches` (`:45`)
- **Today:** `WaitUntilAsync` at `:64` and `:69`. Both fetches are synchronous: the handler throws synchronously with `Retry(0)`, so no delay.
- **Pattern:** P1a + P5.
- **Fix:** After `Subscribe`, assert `Exception != null` and `calls == 1`. After `Invalidate`, assert `Data == "recovered"` and `calls == 2`.
- **Phase:** A.
- **Risk:** none.

### `A_Cache_Hit_Still_Honours_A_Cancelled_Token` (`:74`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

## RevalQuery.Core.Tests/ReviewOf1784595Tests.cs

(The class name is historical. Both tests came in with `8042a8f`.) Fixtures and statics: none.

### `Stopping_The_Collector_After_Disposing_It_Is_Harmless` (`:11`)
- **Today:** it passes deterministically, but it is weak. `RegisterForEviction` starts a `Task.Run` loop. The test only checks that Dispose ×2, then `StopAsync` and `DisposeAsync` don't throw. The fix it was written for (`8042a8f`: the loop disposes its own CTS, so it no longer leaks) is **not observable** through the public API, so the test cannot catch that regression. If the `_isStopped` guards regressed, it would fail only when the loop's `finally` had already disposed the CTS, which is a scheduler race.
- **Pattern:** P9 (+ a hidden P3).
- **Fix (B):**
  1. Once the GC sweep is an `ITimer` armed synchronously, use a `FakeTimeProvider` with `GcTime` 1 s and `GcInterval` 1 s, and count `OnEvictionRequired`.
  2. `RegisterForEviction`, then `Dispose()` ×2, `await StopAsync()`, `await DisposeAsync()`.
  3. `fake.Advance(GcTime + GcInterval)`, then `Assert.Equal(0, evictions)`. Stopped means stopped, deterministically.

  The CTS-leak class of bug disappears with the loop. If B is not adopted: **NEEDS-SIGNAL**. `StopAsync`/`DisposeAsync` would have to await the loop even after a synchronous `Dispose()`, which their docs already promise ("waits for it to finish").
- **Phase:** B.
- **Risk:** none.

### `A_Negative_Retry_Delay_Does_Not_Replace_The_Handler_Failure` (`:25`)
- **Today:** deterministic, no change. The clamp to zero makes `Task.Delay(TimeSpan.Zero)` return an already-completed task, and the handler throws synchronously. Without the clamp, `Task.Delay(-1 s)` throws `ArgumentOutOfRangeException`, so the exact-type `ThrowsAsync` fails.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

## Phase counts

33 tests:
- **Phase A:** 22 tests.
  - RestoreBoundary: 5.
  - Persistence: 2.
  - InvalidateDuringRestore: 2.
  - LoadingDuringRestore: 3.
  - ReviewFindings: 5.
  - A700df3: 3.
  - 45161b7: 2.
- **Phase B:** 2 existing tests, `Resubscribing…Keeps_Polling` and `Stopping_The_Collector…`, plus 1 new test, `Resubscribing_From_Inside_The_Last_Unsubscribe_Keeps_Polling`.
- **ok, no change:** 9 tests.
- **NEEDS-SIGNAL:**
  - The client-level `NotRun` retry in `QueryAsync_Does_Not_Return_Data_It_Never_Fetched`: a seam between `PrepareRun` and `RunAsync`, or keep the documented limitation.
  - Collector-loop completion after `Dispose()`, needed only if B is not adopted.

## Findings

**Suspected library bugs (from reading the code, not reproduced, since the repo is read-only):**
1. **Polling can be lost when a subscriber arrives while the last one is leaving.** `QueryWorker.PausePolling` (`QueryWorker.cs:92`) stops polling unconditionally and never re-checks `Query.HasObservers`. `OnLastSubscriberRemoved` runs the client's `DisposeWorker` before the worker's `PausePolling`. If `TryRelease` refuses because a fetch is in flight, or another thread subscribes between `_observers.Remove` and `DisposeWorker`, the worker stays on the node and the new subscriber's `StartPolling` is a no-op because polling is still active. `PausePolling` then cancels polling under a live subscriber. It recovers on that observer's next `SetOptions` (`ApplyOptions` → `StartPolling`), so Blazor re-renders hide it. A non-rendering observer stays unpolled. The new B test above reproduces it through the public event. Likely fix: re-check `HasObservers` in `PausePolling` under the worker's gate, or restart polling once release is refused. This is the race `085e8e6` labelled "structural / unreachable"; it is reachable.
2. **Minor contract gap.** `TtlQueryGarbageCollector.StopAsync`/`DisposeAsync` say they wait for the loop to finish, but after a synchronous `Dispose()` they return at once (`TtlQueryGarbageCollector.cs:68`) without awaiting `_collectionTask`.
3. **Note, not a bug per the ADR.** The persistence load gets no cancellation token and is not cancelled on `RevalClient.Dispose` (`RevalClient.cs:676`). A gated fake left unreleased leaks its `Task.Run` forever. The TestKit `GatedPersistence` should release on dispose, or tests must always release.

**Where the research doc is wrong or incomplete for these files:**
- **"Every P1 case with persistence can await `RestoreCompleted`" is only half true.** `RestoreCompleted` completes before `LoadPersistedAsync`'s trailing `NotifyChanged` (`RevalClient.cs:689-690`). Tests asserting on notifications or frames (`RestoreBoundary` tests 1 and 3, `LoadingDuringRestore` test 2) need a notification-driven gate. For state assertions it is sufficient, and it even orders after the deferred decision and after a synchronous fetch the decision starts.
- **Open question 2 ("a late restore never overwrites" may need a quiescence signal) is answered: no.** `TryRestore` happens before `CompleteRestore`, which happens before `SetResult`, so `RestoreCompleted` is a sufficient positive signal for those negative assertions.
- **`RestoreBoundaryTests.cs:138` is P1a, not P1.** The restore is over and the handler is synchronous, so `Subscribe` resolves inline.
- **`ReviewFindingsTests.cs:47-53` (P3) is mis-described.** The fetch is already in flight when `QueryAsync` returns, so the 5 ms sleep is not what produces "mid-fetch". More importantly, the test cannot reach the invariant it is named for (see that entry). `ReviewFindingsTests.cs:104` is listed under both P1 and P2; it is P2. `ReviewOfA700df3Tests.cs:49` is "a restore doesn't clear a failure", not "a late restore never overwrites".
- **The doc omits `PersistenceTests.cs:150`** (`Task.Delay(50)` inside `SlowPersistence.AllowLoad`) and the `WaitForStateAsync`/`WaitUntilAsync` uses in `LoadingDuringRestoreTests`, `InvalidateDuringRestoreTests` and `ReviewOf45161b7Tests`.
- **The doc's `ReviewFindingsTests` line numbers for P4** (`:113, :131, :133`) are off. The actual lines are `:114, :129, :136`.

**Tests that cannot fail (or cannot fail for their stated reason):**
- **`InvalidateDuringRestoreTests.Invalidating_While_A_Restore_Is_In_Flight_Still_Refetches`.** It cannot detect "a restore reports Fetching" any more. Since the `_isInvalidated` flag, the deferred decision refetches after the restore anyway. The ADR `loading-spans-restores…` still claims this test pins exactly that, which is now inaccurate. The fix adds the synchronous mid-restore assertion.
- **`RestoreBoundaryTests.A_Restore_Is_Not_Over…`: `Assert.NotEmpty(frames)` (`:90`) is vacuous.**
- **`ReviewFindingsTests.QueryAsync_Does_Not_Return_Data_It_Never_Fetched`.** It cannot reach the `NotRun` retry. The previous documentation of this (`085e8e6`) was removed in `a449568`.
- **`ReviewFindingsTests.Resubscribing…Keeps_Polling`.** Sequential churn never produces the race in its name (also recorded in `085e8e6`). Its poll wait is silent.
- **`ReviewOf1784595Tests.Stopping_The_Collector…`.** The leak it was written for is unobservable, and its guard regressions are caught only by chance.
- **`LoadingDuringRestoreTests.A_Query_Waiting_On_A_Restore…`: `handlerGate` is dead code.** The handler never runs.
- **Today's delay-based versions of the "late restore" and "fresh suppresses" tests can pass falsely when the load is slower than the sleep.**
