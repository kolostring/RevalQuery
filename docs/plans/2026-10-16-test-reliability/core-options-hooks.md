# g4: ObserverSetOptionsTests, MutationSetOptionsTests, RevalHooksTests

The library facts below were checked against the working tree.

- **SetOptions, Observe and hooks.Query run synchronously.** `RevalClient.SetObserverOptions` (`RevalClient.cs:473-509`) attaches under the locks, then calls `QueryWorker.ApplyOptions` (`QueryWorker.cs:123-144`), then `RunIfStale` (`:248-263`). The probe in the research doc covers `Subscribe`, and the same holds for these calls: a completed handler task means the data is applied and the `Idle` notification has fired before the call returns. `QueryAsync` with a `Task.FromResult` handler is synchronous end to end. `RunAsync` completes its TCS before it returns (`QueryWorker.cs:299-333`, persistence is null). `ReleaseIfUnobserved` then runs before `await` resumes (`RevalClient.cs:561-575`).
- **Mutation runs are synchronous until the handler awaits.** `MutationState.ExecuteAsync` sets `Fetching` and raises `OnChanged` synchronously, inside the lock (`MutationState.cs:133-145`). `OnMutate` and the first handler call follow, also synchronously. The final `NotifyChanged` (`:253`) runs before the returned Task completes. `MutationDefault` is 0 retries (`RetryOptions.cs:47`). `Retry(n, _ => TimeSpan.Zero)` is synchronous because `Task.Delay(TimeSpan.Zero, ct)` returns a completed task (`ExponentialBackoffRetryPolicy.cs:34-37`). So the retry tests here do **not** depend on backoff timing.
- **RevalHooks has no asynchrony of its own.** `Query`, `Mutation`, `RenderCompleted`, `Attach` and `Release` (`Hooks/RevalHooks.cs:145-411`) are lock-and-call. Every hooks test is synchronous except where its handler awaits a gate.
- **Notifications land after the state they report.** `ApplyFetched` raises no notification. The next notification is the `Idle` one in `RunFetchAsync`'s finally block (`QueryWorker.cs:383-387`). So `IsResolved` turns true a moment *before* the notification that reports it. This matters for negative assertions; see `Released_Hooks_Do_Not_Tell_The_Renderer`.
- **`QueryState.OnChanged` is a field-like event.** Handlers run in the order they subscribed. Several fixes below rely on this.
- **Every `NewClient()` in these files builds the default `TtlQueryGarbageCollector`.** Its GC loop runs on real time (GcTime 5 min, GcInterval 1 min, `CacheOptions.cs:12-13`). No test here runs long enough to hit it. It is leaked fire-and-forget work (`_ = StopAsync()`, `RevalClient.cs:540`), but it is harmless for these tests.

---

## RevalQuery.Core.Tests/ObserverSetOptionsTests.cs

Shared fixtures and statics in this file:
- `Key1`/`Key2` (`:15-16`) are `static readonly` value tuples. They are immutable, so no change.
- `Item(...)` (`:18-35`): add a `bool neverStale = false` parameter that calls `ConfigureFetch(f => f.NeverStale())`, for the GcTime test. Handlers become `GatedHandler<string>.Handler` or close over a `Counter`.
- `NewClient(...)` (`:37-38`): in Phase B it also takes a `FakeTimeProvider` (`new RevalQueryOptions { TimeProvider = fake }`).
- `RecordingEviction` (`:387-402`): its `List<ITuple>` is not thread-safe, but every call into it comes synchronously from the test thread (`RevalClient.cs:604-608, 617`). Keep it.
- Every `TestUtils.WaitForStateAsync` use in this file either returns at once because the predicate is already true (P1a), or is a real lost-wakeup race (`:243`). `TestUtils.WaitUntilAsync` (`:88`) is the silent poller.

### `The_Same_Key_Reapplies_Options_Without_Refetching` (`ObserverSetOptionsTests.cs:44`)
- **Today:** `WaitForStateAsync` at `:56`, which returns at once because `Subscribe` already resolved the query synchronously. The negative assertions "no refetch" (`:66-67`) run straight after `SetOptions`.
- **Pattern:** P1a. P9 for the negative assertion: it is partly vacuous.
- **Fix:**
  - Phase A: delete `:56` and assert `IsResolved`, `IsIdle` and `calls == 1` right after `Subscribe`. Keep `:60-67` unchanged; the "same key, already enabled" decision is synchronous (`QueryWorker.cs:125-143`).
  - Phase B, to make the negative assertion bite: the rebuilt options set `StaleTime(1h)`. Any wrongly added `RunIfStale` would therefore see fresh data and never fetch, so the test cannot catch "SetOptions refetches when stale". Add a second step under `FakeTimeProvider`:
    1. `SetOptions(options with StaleTime(1 min))`.
    2. `fake.Advance(2 min)` so the data is stale.
    3. `SetOptions` again with the same key.
    4. Assert `calls == 1` and `IsIdle`.

    This is TanStack's `shouldFetchOptionally`: same query and previously enabled means no fetch.
- **Phase:** B (the A part needs no fake time).
- **Risk:** Keep `Assert.Same(before, observer.Query)` and the StaleTime read-back. They are what prove "re-applied, not moved".

### `Switching_To_A_Key_Held_Only_By_Disabled_Observers_Starts_Polling` (`:71`)
- **Today:** 40 ms real `RefetchInterval`. `TestUtils.WaitUntilAsync(calls >= 3, 2000)` (`:88`) polls every 10 ms and **returns silently** at the deadline. `:90` then asserts the same predicate.
- **Pattern:** P4 and P5. The research doc misses this site: it is not in the P4 polling list or the P5 list.
- **Fix (B, fake time with poll timers armed synchronously):**
  1. Use two `Counter`s, `key1Calls` and `key2Calls`.
  2. Subscribe `disabled` (key 2, Enabled false) and assert `key2Calls == 0`.
  3. Subscribe `switching` (key 1) and assert `key1Calls == 1`.
  4. Call `switching.SetOptions(Item(2, ...))` and assert `key2Calls == 1` straight away. The `moved` path calls `RunIfStale` synchronously (`RevalClient.cs:508`).
  5. `fake.Advance(interval)`, then assert `key2Calls == 2`.
  6. `fake.Advance(interval)`, then assert `key2Calls == 3`. Use exact equality, not `>=`.
  7. Assert `key1Calls == 1` after both advances. This proves key 1 stopped polling when the observer left it (`PausePolling` via `OnLastSubscriberRemoved`).
- Interim Phase A step, before B lands, so a failure is at least loud: replace `:88-90` with `await Signals.UntilAsync(client.FindQuery(Key2)!, _ => key2Calls.Value >= 3)`. Every poll fetch raises `OnChanged`. This is not the fix, because the library still runs on wall-clock time.
- **Phase:** B.
- **Risk:** This is the regression test for `8a1bbf2`. It is subtle because the switch adds a *non-first* subscriber, so `OnFirstSubscriberAdded`/`StartPolling` does not fire. `observer.Enabled` is also set before `ApplyOptions` reads `wasEnabled` (`RevalClient.cs:495`), so `wasEnabled == true`. Only the unconditional `StartPolling` at `QueryWorker.cs:141` starts the poll. The B version has to keep that path: do not "fix" it by subscribing an enabled observer.

### `The_Same_Key_Enabling_A_Disabled_Observer_Fetches` (`:94`)
- **Today:** `WaitForStateAsync` at `:109`, which returns at once.
- **Pattern:** P1a.
- **Fix:** Delete `:109`. Right after `SetOptions(enabled: true)`, assert `observer.Query.IsResolved`, `IsIdle` and `calls == 1`. `ApplyOptions` goes `!wasEnabled` → `RunIfStale` → synchronous fetch (`QueryWorker.cs:143`). Keep `:106` (`calls == 0` after the disabled `Subscribe`): that negative is a synchronous decision.
- **Phase:** A.
- **Risk:** None.

### `A_New_Key_Moves_The_Observer_To_That_Query_And_Fetches_It` (`:115`)
- **Today:** `WaitForStateAsync` at `:122` and `:127`. Both return at once.
- **Pattern:** P1a.
- **Fix:** Delete both waits and assert `IsResolved` straight after `Subscribe` and after `SetOptions`. `Registered`/`Cancelled` are filled synchronously by `Detach`/`Attach` (`RevalClient.cs:604-608`). The `NotifyChanged` counting at `:139-144` is synchronous and nothing runs in the background (synchronous handler, no polling), so it stays as is. Use `Counter` for `changes`.
- **Phase:** A.
- **Risk:** The `changes` delta assertions are exact only because there is no background notifier. Keep the handler synchronous.

### `A_New_Key_That_Is_Disabled_Does_Not_Fetch` (`:148`)
- **Today:** `WaitForStateAsync` at `:160` and `:169`. Both return at once.
- **Pattern:** P1a.
- **Fix:** Delete both waits and assert `IsResolved` right after `Subscribe` and after the second `SetOptions`. The negative `fetched == [1]` (`:166`) is a synchronous decision (`ApplyOptions` returns at `QueryWorker.cs:133-137`).
- **Phase:** A.
- **Risk:** None.

### `Switching_Back_Within_GcTime_Reuses_The_Cached_Query` (`:175`)
- **Today:** `WaitForStateAsync` at `:190` and `:194`, which return at once. `calls == 2` at `:203` ("no refetch on switch back") depends on `StaleTime(1h)` against the **real clock**: `QueryState.cs:293` reads `DateTimeOffset.UtcNow`.
- **Pattern:** P1a, plus P4 (wall-clock staleness hidden behind a large margin).
- **Fix:**
  1. Delete both waits; assert straight after each call.
  2. Replace `staleTime: hour` with the new `neverStale: true`. `IsStale` then returns false through `fetchOptions.Static` (`QueryState.cs:290`) without reading the clock. Staleness is incidental to what this test proves (identity reuse plus no refetch), so no fake time is needed.
  3. Keep `Assert.Same(first, observer.Query)`, the `Cancelled` contains `Key1` check, and `calls == 2`.
- **Phase:** A.
- **Risk:** The name says GcTime, but GcTime is never exercised: `RecordingEviction` never evicts on its own. That is fine and deterministic, but it would read better as "before eviction". If the maintainer wants the stale-margin form kept, it moves to B (`StaleTime(1 min)` with no `Advance`).

### `Switching_While_The_Old_Fetch_Runs_Neither_Cancels_It_Nor_Hears_From_It` (`:207`)
- **Today:** Hand-rolled gates, `oldStarted`/`releaseOld` (`:209-210`, with RunContinuationsAsynchronously; good). `await oldStarted.Task` (`:228`) has no hang guard. `WaitForStateAsync(second)` (`:234`) returns at once. `releaseOld.SetResult()` (`:242`) is followed by `WaitForStateAsync(first, IsResolved && IsIdle)` (`:243`). That second wait is a **real lost-wakeup race**: the continuation on the pool can finish the fetch between the helper's first check (`TestUtils.cs:31`) and its subscribe (`:36`), and the test then times out after 2 s. The helper's TCS also has no `RunContinuationsAsynchronously`, so the test resumes inline inside `first.NotifyChanged()` on the pool thread, before `RunCoreAsync` has completed `_inFlight` or fired `OnReleaseDue`, and then disposes the client from inside the library's notification.
- **Pattern:** P5, P1a (`:234`) and P8 (no guard on `:228`).
- **Fix:**
  1. `var old = new GatedHandler<string>()`. The key-1 fetch uses `old.Handler`, and key 2 returns `Task.FromResult`.
  2. `Subscribe`, then `await old.Started(0).WaitAsync(HangGuard)`. It is already complete, because the fetch starts synchronously. Assert `first.IsFetching`.
  3. `SetOptions(Item(2))`, then assert at once: `second.IsResolved`, `observer.Query.Data == "data-2"`, `first.IsFetching`, and `!old.TokenOf(0).IsCancellationRequested`. GatedHandler needs to expose the token per call; otherwise keep capturing it in the closure.
  4. `var heard = changes.Value`.
  5. **Before releasing**, `var settled = Signals.UntilAsync(first, s => s.IsResolved && s.IsIdle)`. Its first check is false, because the gate is closed. It can then complete only from an `OnChanged` raised after the release, and that event runs every earlier subscriber first.
  6. `old.Release(0, "data-1")`, then `await settled`.
  7. Assert `first.Data == "data-1"`, `changes.Value == heard`, and `Assert.Same(second, observer.Query)`.
- **Phase:** A.
- **Risk:** The negative assertion `changes == heard` holds only if the wait completes on the final `Idle` notification and not on an earlier check. Subscribing the signal before the release guarantees that. A check-first wait started after the release could see `IsResolved && IsIdle` set (`QueryWorker.cs:368, 385`) before `NotifyChanged` (`:386`) runs, and so pass early. Keep `oldToken` not cancelled: it proves the leave path does not cancel.

### `A_Result_Type_Mismatch_Throws_Before_The_Observer_Moves` (`:251`)
- **Today:** `WaitForStateAsync` at `:258`, which returns at once. `await client.QueryAsync(... Task.FromResult(7))` (`:261`) is synchronous.
- **Pattern:** P1a.
- **Fix:** Delete `:258` and assert `IsResolved` after `Subscribe`. The rest is synchronous: `GetOrCreateQueryLocked` throws before `Attach` (`RevalClient.cs:97-103`, `:491`). Use `Counter` for `changes`.
- **Phase:** A.
- **Risk:** `eviction.Registered.Clear()` (`:262`) is race-free only because `QueryAsync` ran `ReleaseIfUnobserved` synchronously. With an async handler it would also be safe, because `await` resumes after the finally block (`RevalClient.cs:569-572`), but keep the handler synchronous.

### `Disposing_After_A_Switch_Releases_The_New_Query_And_Is_Idempotent` (`:278`)
- **Today:** `WaitForStateAsync` at `:284` and `:288`. Both return at once.
- **Pattern:** P1a.
- **Fix:** Delete both waits and assert `IsResolved` straight after each call. The rest is synchronous.
- **Phase:** A.
- **Risk:** None.

### `SetOptions_After_The_Observer_Is_Disposed_Throws` (`:301`)
- **Today:** Deterministic, no change. It is synchronous; `ThrowIfDisposedLocked` runs under `observer.Gate` (`RevalClient.cs:483-485`).
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `SetOptions_After_The_Client_Is_Disposed_Throws` (`:313`)
- **Today:** Deterministic, no change. `GetOrCreateQueryLocked` throws `ODE` for either key (`RevalClient.cs:92`).
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Switching_Back_To_An_Evicted_Key_Creates_A_Fresh_Query` (`:327`)
- **Today:** `WaitForStateAsync` at `:340`, `:344` and `:351`. All return at once. `eviction.Evict(Key1)` runs synchronously (`HandleEviction`, `RevalClient.cs:640`).
- **Pattern:** P1a.
- **Fix:** Delete the three waits and assert `IsResolved` right after each call. Keep `calls == 3`.
- **Phase:** A.
- **Risk:** None.

### `Observe_Creates_On_The_First_Call_Then_Reapplies_And_Switches` (`:360`)
- **Today:** `WaitForStateAsync(moved)` at `:380`, which returns at once.
- **Pattern:** P1a.
- **Fix:** Delete `:380` and assert `moved.IsResolved` and `Data == "data-2"` right after the third `Observe`.
- **Phase:** A.
- **Risk:** None.

### `ObserverSetOptionsRaceTests.Switching_Against_Eager_Eviction_Always_Lands_On_A_Registered_Query` (`:411`)
- **Today:** A stress test in the `"stress"` collection. The churn runs on `Task.Run` (`:424-434`); the test does 2000 synchronous rounds. There are three hidden problems:
  1. **The churn may never overlap the rounds.** The rounds are synchronous and fast, and nothing waits for the churn to start, so on a loaded pool the test can pass without any contention.
  2. **A spurious failure path.** The churn catches only `OperationCanceledException`. `QueryAsync` throws `InvalidOperationException("could not be run ...")` after `MaxRunAttempts = 3` consecutive releases (`RevalClient.cs:28, 559-581`). A round that switches away disposes the worker the churn just prepared, so this can happen, and `await churn` then fails the test for a reason unrelated to the invariant.
  3. **A failure in the `try` is masked.** If the churn has thrown, the `await churn` in `finally` replaces an assertion failure from the `try`.
- **Pattern:** Stress (topic 5), P3 in the sense that it relies on scheduling for overlap.
- **Fix:**
  1. Add `var churning = new Gate()`; the churn calls `churning.Open()` after its first completed `QueryAsync`.
  2. Await `churning.Task.WaitAsync(HangGuard)` before the rounds loop.
  3. In the churn, also catch `InvalidOperationException` whose message contains "could not be run", and count it in a `Counter`. That outcome is documented, not a defect.
  4. In `finally`, capture the churn's exception separately instead of letting it replace the round's assertion failure. One way: `stop.Cancel(); var churnError = await Record.ExceptionAsync(() => churn);`, then after the `finally` block `Assert.Null(churnError)`.
  5. Keep it labelled as a probabilistic regression net. The invariant holds by lock: `Attach` happens under `_gate` in `SetObserverOptions` (`RevalClient.cs:489-501`), and `HandleEviction` checks `HasObservers` under the same lock. No gate can observe that window, so a deterministic interleaving test is not possible. That is the documented limitation.
- **Phase:** A.
- **Risk:** Do not remove the race test in favour of a sequential one. The sequential form (`QueryAsync` → evicted → `Observe` the same key) is worth adding as a deterministic companion.

---

## RevalQuery.Core.Tests/MutationSetOptionsTests.cs

Shared fixtures and statics in this file:
- `Signal()` (`:17`) is already a `RunContinuationsAsynchronously` TCS → replace with `Gate`. Each `started`/`release` pair → one `GatedHandler<string>`, using `Started(0)` and `Release(0, v)`/`Fail(0, ex)`.
- `Logging(...)` (`:22-33`) logs to a `ConcurrentQueue`. Keep it.
- There are no statics. None of the `await started.Task` or `await run` calls has a hang guard; add `.WaitAsync(HangGuard)` to each (P8).
- `GatedHandler<T>` must also have an adapter for `MutationHandlerExecutionContext<TParams>`. It reads the token from `ctx.CancellationToken`.

### `Options_Set_During_The_Handler_Fire_Their_Callbacks_And_The_Old_Ones_Do_Not` (`MutationSetOptionsTests.cs:36`)
- **Today:** Gated (`:39-48`), with no sleeps. `OnMutate` runs before the handler starts, so "old:mutate" is deterministic. After `SetOptions`, `run.Current` is the new options when the callbacks read it (`MutationState.cs:184, 239`). The test is deterministic, but `:51` and `:55` have no guard.
- **Pattern:** ok, plus P8.
- **Fix:**
  1. `var h = new GatedHandler<string>()`.
  2. `run = ExecuteAsync(a)`, then `await h.Started(0).WaitAsync(HangGuard)`.
  3. `SetOptions(new)`, then `h.Release(0, "a")`.
  4. `await run.WaitAsync(HangGuard)`.
  5. Assert the same exact log sequence and `Data == "a"`.
- **Phase:** A.
- **Risk:** The exact sequence check is what proves the old `OnResolved`/`OnSettled` did not fire. Keep `Assert.Equal` on the whole array; do not switch to `Contains`.

### `Options_Set_During_The_Handler_Also_Reroute_A_Failure` (`:64`)
- **Today:** Gated and deterministic. It depends on `MutationDefault` being 0 retries (`RetryOptions.cs:47`). With retries, the next attempt would call the *new* handler and resolve.
- **Pattern:** ok, plus P8.
- **Fix:** Same as above, but release with `h.Fail(0, new InvalidOperationException("boom"))`. Optionally set `.ConfigureRetry(r => r.Retry(0))` explicitly, so the test no longer depends on the default.
- **Phase:** A.
- **Risk:** The implicit dependency on the zero-retry default: a change to the default would turn this into a confusing failure.

### `Options_Set_Between_Retry_Attempts_Supply_The_Next_Handler_But_Not_The_Retry_Count` (`:90`)
- **Today:** Gated first attempt. `Retry(2, _ => TimeSpan.Zero)` makes `Task.Delay(0)` synchronous, so attempts 2 and 3 run inline on the thread that released the gate. Deterministic, no backoff timing.
- **Pattern:** ok, plus P8.
- **Fix:**
  1. `h.Started(0)` guarded, then `SetOptions(newHandler, Retry(0))`.
  2. `h.Fail(0, new InvalidOperationException("first attempt"))`.
  3. `await run.WaitAsync(HangGuard)`.
  4. Assert `newCalls.Value == 2`, `IsException`, and the message "later attempt". Use `Counter` for `newCalls`.
- **Phase:** A.
- **Risk:** `newCalls == 2` proves the retry count was fixed at start (`MutationState.cs:151`). Keep the zero delay. A non-zero `RetryDelay` would bring P4 in, and would need B (`Task.Delay(delay, timeProvider, ct)`).

### `A_Handler_Set_Between_Attempts_Can_Be_The_One_That_Succeeds` (`:127`)
- **Today:** Same structure, `Retry(1, zero)`. Deterministic.
- **Pattern:** ok, plus P8.
- **Fix:** `GatedHandler` with `Fail(0, ...)` after `SetOptions`, then a guarded `await run`, then the same asserts.
- **Phase:** A.
- **Risk:** None.

### `Of_Two_Concurrent_Runs_Only_The_Latest_Reads_The_New_Options` (`:156`)
- **Today:** Four gates. Deterministic: each run's `RunOptions` is a separate object, and `SetOptions` updates only `_latestRun`, which is run B, set synchronously in B's `ExecuteAsync` (`MutationState.cs:137`). The release order does not matter. `await startedA/B` and `Task.WhenAll` have no guards.
- **Pattern:** ok, plus P8.
- **Fix:**
  1. Use one `GatedHandler<string>`: call 0 is A, call 1 is B.
  2. `await h.Started(0)`, then start B, then `await h.Started(1)` (both guarded).
  3. `SetOptions`, then `h.Release(0, "a")`, then `h.Release(1, "b")`.
  4. `await Task.WhenAll(runA, runB).WaitAsync(HangGuard)`.
  5. Keep the asserts.
- **Phase:** A.
- **Risk:** `GatedHandler` call indices must follow handler invocation order. That holds here because the test awaits `Started(0)` before it starts B.

### `Per_Call_Callbacks_Are_Not_Touched_By_New_Options` (`:197`)
- **Today:** Gated and deterministic.
- **Pattern:** ok, plus P8.
- **Fix:** Convert to `GatedHandler` with a guarded await. Tighten the assertion to the exact sequence `["old:mutate:a", "new:resolved:a", "call:resolved:a", "new:settled:a", "call:settled:a"]`, which is the order in `MutationState.cs:184-188, 239-242`. That shows the per-call callbacks fired *alongside* the new options rather than being replaced.
- **Phase:** A.
- **Risk:** Today the test checks only `Contains`. It could not tell "per-call callbacks were swapped for new ones" from "fired", but `MutateOptions` cannot be reached by `SetOptions` anyway. The exact sequence makes the test stronger.

### `Options_Set_While_Idle_Are_Used_By_The_Next_Run` (`:227`)
- **Today:** Deterministic, no change. Both `ExecuteAsync` calls complete synchronously (handler `Task.FromResult`).
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Options_Set_After_A_Run_Settled_Do_Not_Reach_It_And_Reach_The_Next` (`:246`)
- **Today:** Gated and deterministic. `_latestRun = null` runs in the finally block before the Task completes (`MutationState.cs:247-250`). There are no guards on `:261` and `:263`.
- **Pattern:** ok, plus P8.
- **Fix:** `GatedHandler`; guarded `Started(0)`; `Release(0, "a")`; guarded `await first`; then the rest unchanged.
- **Phase:** A.
- **Risk:** None.

### `CreateMutation_Gives_Handlers_The_Clients_Service_Provider` (`:274`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `CreateMutation_After_The_Client_Is_Disposed_Throws` (`:294`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Observe_Creates_On_The_First_Call_Then_Reapplies_Options` (`:304`)
- **Today:** Synchronous and deterministic. `changes > 0` (`:326`) is a weak check: the exact count is known, `Fetching` (`MutationState.cs:143`) plus the final notification (`:253`), which makes 2.
- **Pattern:** ok (a weak assertion).
- **Fix:** Use `Counter changes` and assert `Assert.Equal(2, changes.Value)`. Also pass a second `Counter` to the second `Observe` call and assert it stays 0. That proves the callback is used only when the slot is created.
- **Phase:** A.
- **Risk:** None.

### `Hooks_Apply_The_Options_Of_Every_Render` (`:330`)
- **Today:** Deterministic, no change. It is synchronous.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

---

## RevalQuery.Core.Tests/RevalHooksTests.cs

Shared fixtures and statics in this file:
- `Signal()` (`:16`) → `Gate`.
- `Gated(key, gate)` (`:28-33`) → `QueryOptions.Create<string>(key, h.Handler)` with a `GatedHandler<string>`. Today the handler ignores its token.
- `Opts(key, onCall)` (`:21-26`) is used with `calls++`, which is not atomic. All of those calls are synchronous on the test thread, so this is harmless; switch to `Counter` anyway for uniformity.
- `int changed` with `changed++` in callbacks (`:433`, `:499`) → `Counter`.
- There are no statics.

### `A_Key_That_Stops_Being_Read_Is_Released_At_The_Next_Sweep` (`RevalHooksTests.cs:44`)
- **Today:** Deterministic, no change. Hooks and synchronous handlers only.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Keys_Added_And_Removed_In_A_Loop_Follow_What_The_Loop_Read` (`:63`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Helper_Called_Twice_With_Different_Keys_Keeps_Both` (`:81`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Two_Calls_On_One_Line_Keep_Both` (`:98`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** It relies on `[CallerLineNumber]` giving the same site for both calls. It is still deterministic.

### `A_Site_That_Was_Not_Read_Releases_Nothing` (`:114`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Read_Between_Sweeps_Counts_Towards_The_Next_One` (`:129`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `The_Same_Key_Twice_In_A_Render_Shares_One_Observer` (`:145`)
- **Today:** Deterministic, no change. `calls == 1` holds because the second read finds the existing entry: the same key is already enabled, so there is no fetch.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `The_Same_Key_At_Two_Sites_Shares_One_Observer_Until_Neither_Holds_It` (`:164`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `An_Explicit_Slot_Is_An_Identity_Of_Its_Own` (`:189`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `The_Builder_Overloads_Read_Like_The_Options_Ones` (`:208`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_String_Slot_Is_A_Slot_And_Never_A_File_Name` (`:223`)
- **Today:** Deterministic, no change. The final two `hooks.Mutation` calls (`:238-239`) assert nothing; they only prove the call compiles and does not throw.
- **Pattern:** ok.
- **Fix:** None for timing. Optionally assert `Assert.Same` on the two mutation states, so the tail checks that the string slot maps to one mutation.
- **Phase:** none needed.
- **Risk:** None.

### `A_Key_Read_With_Another_Result_Type_Throws` (`:243`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Repeat_Read_Applies_The_Options_Of_That_Render` (`:255`)
- **Today:** `WaitForStateAsync(state, IsResolved)` at `:274`, which returns at once. The repeat read goes `SetOptions` → `ApplyOptions` → `!wasEnabled` → synchronous fetch.
- **Pattern:** P1a.
- **Fix:** Delete `:274` and assert `state.IsResolved` and `calls == 1` straight after the second `hooks.Query`.
- **Phase:** A.
- **Risk:** None.

### `A_Mutation_Is_Not_Released_By_A_Sweep` (`:279`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Repeat_Mutation_Read_Applies_The_New_Options` (`:294`)
- **Today:** Deterministic, no change. `ExecuteAsync` completes synchronously.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Mutations_Are_Told_Apart_By_Key_And_By_Call_Site` (`:308`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Running_Mutation_Survives_Sweeps_And_Reports_To_The_Host` (`:324`)
- **Today:** Gated (`:330-338`), deterministic, but the checks are weak: `changed > 0` (`:346`) and `changed > before` (`:352`). `:341` and `:350` have no guards.
- **Pattern:** ok, plus P8 (a weak assertion).
- **Fix:**
  1. `GatedHandler<string>`; `run = state.ExecuteAsync(a)`; guarded `await h.Started(0)`.
  2. Two `RenderCompleted()` calls.
  3. Assert `state.IsFetching` and `changed.Value == 1`. The `Fetching` notification fires synchronously inside `ExecuteAsync`.
  4. `h.Release(0, "done")`, then `await run.WaitAsync(HangGuard)`.
  5. Assert `changed.Value == 2` and the `Data` read-back.
- **Phase:** A.
- **Risk:** None.

### `A_Change_The_Render_Did_Not_Read_Reaches_The_Host` (`:357`)
- **Today:** Gated query. `Assert.True(changed.Task.IsCompleted)` (`:367`) holds synchronously, because the `Fetching` notification is raised inside `hooks.Query`. `changed` is reassigned (`:368`) and then captured by the host callback. That is safe: the write happens before `gate.SetResult` (a full fence), and the gate is still closed, so no notification can occur in between. `await changed.Task` (`:371`) has **no hang guard**.
- **Pattern:** ok, plus P8.
- **Fix:**
  1. `GatedHandler<string>`. `var first = new Gate()`, `var second = new Gate()`. The host callback opens `first` if it is not yet open, and otherwise `second`; or use a `Counter` with `Signals`. This avoids reassigning a captured local.
  2. Assert `first.Task.IsCompleted`.
  3. `h.Release(0, "slow")`, then `await second.Task.WaitAsync(HangGuard)`.
  4. Assert `state.IsResolved`. The `Idle` notification comes after `ApplyFetched`, so it is deterministic.
- **Phase:** A.
- **Risk:** None.

### `A_Change_Raised_While_Reading_Is_Forwarded_Like_Any_Other` (`:377`)
- **Today:** Synchronous. `changed > 0` (`:387`); the exact value is 2 (`Fetching` and `Idle`). The negative assertion `settled == changed` after a repeat read (`:389-391`) holds by the synchronous "same key, already enabled → no fetch" decision. Its ability to fail, however, rests on the **real clock**: with the default `StaleTime` of 0, a wrongly added `RunIfStale` would refetch only if `UtcNow` has moved past `_lastUpdatedAt`, because `QueryState.cs:293` uses a strict `>`. That is almost always true on the real clock. Under a `FakeTimeProvider` with no `Advance`, it is **always false**, so once B lands the negative assertion silently becomes vacuous.
- **Pattern:** ok today. P4 latent, and P9 under B.
- **Fix:**
  - Phase A: assert `changed.Value == 2` exactly.
  - Phase B: `fake.Advance(TimeSpan.FromTicks(1))` before the repeat read, so the data is provably stale, then assert the count is unchanged.
- **Phase:** B.
- **Risk:** See Findings 1: `>` versus TanStack's `>=`. If the library moves to `>=`, the Advance is no longer needed, but it does no harm.

### `A_Change_Before_Any_Host_Is_Delivered_Once_On_Attach` (`:395`)
- **Today:** Deterministic, no change. The mutation completes synchronously, sets `_pending`, and `Attach` flushes once.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Nothing_Is_Delivered_On_Attach_When_Nothing_Changed` (`:410`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `A_Second_Attach_Is_Refused_While_One_Is_Attached` (`:422`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Released_Hooks_Do_Not_Tell_The_Renderer` (`:433`)
- **Today:** `gate.SetResult()` (`:445`) is followed by `TestUtils.WaitForStateAsync(state, IsResolved)` (`:446`). That is a **real lost-wakeup race**: the fetch can complete on the pool between the helper's check and its subscribe, and the test then hangs for 2 s and throws `TimeoutException`. It is also a false-pass window for the negative assertion `changed == 0` (`:448`). `IsResolved` becomes true in `ApplyFetched` *before* the final `NotifyChanged` (`QueryWorker.cs:368` vs `:386`). A check-first wait can therefore succeed before the last notification, and the assertion then runs before a hypothetically leaking host call would have landed.
- **Pattern:** P5, plus a P2-style negative.
- **Fix:**
  1. `var h = new GatedHandler<string>()`; `var changed = new Counter()`.
  2. `handle = hooks.Attach(() => changed.Increment())`; `state = hooks.Query(Create("late", h.Handler))`.
  3. `handle.Dispose()`, then `changed.Reset()` or record a baseline.
  4. **Before releasing**: `var settled = Signals.UntilAsync(state, s => s.IsResolved && s.IsIdle)`. The first check is false, because the gate is closed. It can then complete only inside the final `OnChanged`. Any leftover observer handler subscribed before the signal's handler, so it has already run by then.
  5. `Assert.False(settled.IsCompleted)`.
  6. `h.Release(0, "late")`, then `await settled`.
  7. Assert `changed.Value == 0` and `state.Data == "late"`.
- **Phase:** A.
- **Risk:** The ordering argument depends on (a) `OnChanged` being a multicast delegate invoked in subscription order, and (b) `Signals.UntilAsync` evaluating its predicate inside the event handler, not in a later continuation. Write both into `Signals`' doc comment. The test passes if any of three layers holds: `Detach` removed the handler, `RevalHooks.OnObserverChanged` checks `_isReleased`, or `_host = null`. So it cannot tell which layer works. That is acceptable for a behaviour test.

### `Disposing_The_Attach_Handle_Releases_Every_Observer` (`:452`)
- **Today:** Deterministic, no change. It creates a mutation (`:460`) but asserts nothing about it.
- **Pattern:** ok (a weak check).
- **Fix:** None for timing. Optionally: run the mutation after `Dispose`, and with a `Counter` on the attach callback, assert that `ExecuteAsync` does not reach the host. This proves the `MutationObserver` detached; it is synchronous.
- **Phase:** none needed.
- **Risk:** None.

### `Disposing_The_Attach_Handle_Twice_Is_A_No_Op` (`:469`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Released_Hooks_Refuse_Reads_And_Attach` (`:483`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Released_Hooks_Ignore_Sweeps_And_Notifications` (`:499`)
- **Today:** Same as `Released_Hooks_Do_Not_Tell_The_Renderer`: `WaitForStateAsync` at `:514` is a lost-wakeup race and a false-pass window for `changed == 0` (`:516`).
- **Pattern:** P5, plus a P2-style negative.
- **Fix:** Identical to `Released_Hooks_Do_Not_Tell_The_Renderer`, with `hooks.RenderCompleted()` between `handle.Dispose()` and creating `settled`. `RenderCompleted` returns early when released (`RevalHooks.cs:312`). Also assert that it does not throw.
- **Phase:** A.
- **Risk:** As above.

### `Hooks_Are_Not_Disposable` (`:520`)
- **Today:** Deterministic, no change. It is a reflection check.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `AddRevalQuery_Gives_Every_Resolution_Its_Own_Hooks` (`:526`)
- **Today:** Deterministic, no change. The client built through DI starts a real TTL GC loop, which is never reached within the test.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

### `Creating_Hooks_On_A_Disposed_Client_Throws` (`:546`)
- **Today:** Deterministic, no change.
- **Pattern:** ok.
- **Fix:** None.
- **Phase:** none needed.
- **Risk:** None.

---

## Counts

| File | Tests | A | B | no change | NEEDS-SIGNAL |
|---|---|---|---|---|---|
| ObserverSetOptionsTests.cs (with RaceTests) | 14 | 10 | 2 | 2 | 0 |
| MutationSetOptionsTests.cs | 12 | 8 | 0 | 4 | 0 |
| RevalHooksTests.cs | 28 | 5 | 1 | 22 | 0 |
| **Total** | **54** | **23** | **3** | **28** | **0** |

Most Phase A items in the mutation file are only "add a HangGuard and convert to GatedHandler". The real flakes, where a slow machine can turn a pass into a failure, are:
- **Lost-wakeup races**, at three sites: `ObserverSetOptionsTests.cs:243`, `RevalHooksTests.cs:446` and `RevalHooksTests.cs:514`.
- **The wall-clock polling test**, `ObserverSetOptionsTests.cs:71-91`.
- **The stress test's spurious `InvalidOperationException` path.**

## Findings

1. **Suspected library bug: staleness boundary.** `QueryState.IsStale` uses `UtcNow - _lastUpdatedAt > StaleTime` (`QueryState.cs:293`). TanStack treats data as stale once `now >= updatedAt + staleTime`: `isStaleByTime` → `!timeUntilStale(...)`, where `timeUntilStale = max(updatedAt + staleTime - now, 0)` (utils.ts, cited in the research doc at L158). This is from my reading of TanStack; the maintainer should confirm it at `ee47993`. As a result, with the default `StaleTime` of 0, data is *fresh* for as long as the clock has not ticked. On the real clock that is a tiny window. Under `FakeTimeProvider` (Phase B) it is permanent until `Advance`, so every "StaleTime 0 means always stale" test and negative assertion changes meaning. Decide this before or together with the TimeProvider ADR.
2. **`MutationState<,>` does not implement `IMutationState`.** The interface exists (`Abstractions/Mutation/IMutationState.cs:7, 45`), but nothing implements it (grep across Core and Blazor). The brief's `Signals.UntilAsync(IMutationState, …)` therefore cannot accept a `MutationState`. TestKit needs an overload on `MutationState<TParams,TRes>`; the alternative is a library decision to implement the interface. The interface looks dead; flag it to the maintainer.
3. **A spurious failure path in `ObserverSetOptionsRaceTests`.** `QueryAsync` can throw `InvalidOperationException` after `MaxRunAttempts = 3` (`RevalClient.cs:28, 559-581`) while the test's switches keep disposing the worker the churn just prepared. The churn catches only OCE, so this fails the test for a reason unrelated to the invariant. That is documented behaviour, not a bug. However, `MaxRunAttempts` is a count-based give-up, and whether it is acceptable under eager eviction is a design question.
4. **Research-doc corrections for these files:**
   - `ObserverSetOptionsTests.cs:88`, a silent `WaitUntilAsync` against a 40 ms real `RefetchInterval`, is missing from P4 and P5.
   - The `WaitForStateAsync` line numbers in P5 are off by two. The check is at `TestUtils.cs:31`, the TCS at `:33` and the subscribe at `:36`, not `:29`/`:31`/`:34`.
   - The P5 description is right, and in these files it is not theoretical: three of the 22 `WaitForStateAsync` uses wait for a change that happens on the pool after a gate opens, so they can lose the wakeup. The other 19 are P1a, returning at once.
   - The doc's P1a claim generalises beyond `Subscribe` to `SetOptions`, `Observe` and `hooks.Query`, all verified (`RevalClient.cs:473-509`, `QueryWorker.cs:123-144`).
5. **Tests that cannot fail, or can only partly fail:**
   - `The_Same_Key_Reapplies_Options_Without_Refetching` cannot catch "SetOptions refetches when stale", because it sets `StaleTime(1h)` before asserting no refetch. The fix is in Phase B.
   - `A_Change_Raised_While_Reading_Is_Forwarded_Like_Any_Other`: its negative assertion bites today only because the real clock ticks (`>` at `QueryState.cs:293`). It becomes vacuous under fake time unless it advances.
   - Weak, but they can fail: `Observe_Creates_On_The_First_Call_Then_Reapplies_Options` (`> 0`), `A_Running_Mutation_Survives_Sweeps...` (`> 0`, `> before`), `Per_Call_Callbacks_Are_Not_Touched...` (`Contains` only), `Disposing_The_Attach_Handle_Releases_Every_Observer` (the mutation is not checked), and the tail of `A_String_Slot_...`, which asserts nothing.
6. **Hidden asynchrony that is benign in these files:** the default TTL GC background loop, started by every `NewClient()` and stopped fire-and-forget (`RevalClient.cs:540`). It never reaches an eviction within these tests, because GcTime is 5 min. There are no static counters and no `DateTime.UtcNow.Ticks` identities in these three files.
