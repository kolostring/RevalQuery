# G1: per-test reliability plan (Core: cancellation, QueryAsync, retry)

Scope: `RevalQuery.Core.Tests/` CancellationTests.cs, HandlerCancellationTests.cs, OrphanedFetchTests.cs, QueryAsyncTests.cs, QueryAsyncDiscardedTests.cs, QueryAsyncIntegrationTests.cs, RetryCountTests.cs, TestUtils.cs. 39 test methods.

## Library facts these entries rely on (verified in code)

- **S1. A fetch starts synchronously, and a completed handler settles it synchronously.** `QueryWorker.RunAsync` (`QueryWorker.cs:275-301`) calls `RunCoreAsync` without yielding. `RunFetchAsync` (`:335-364`) calls the handler through `ExponentialBackoffRetryPolicy`, which awaits nothing before attempt 0 (`ExponentialBackoffRetryPolicy.cs:30-42`). So a `Task.FromResult`/`Task.FromException`/synchronously-throwing handler has applied its data or failure, and set Idle, before `RunAsync` returns. `completion` uses `RunContinuationsAsynchronously` (`:292`), but that only affects continuations registered *before* it completes. A `QueryAsync` that awaits an already-completed task continues inline, and `.WaitAsync(ct)` on a completed task returns it. So **`QueryAsync` with a synchronous handler returns an already-completed Task** (`RevalClient.cs:224-256`, `:558-576`).
- **S2. Join or start is decided synchronously.** A second `QueryAsync` while a fetch is in flight returns the running task (`QueryWorker.cs:285-287`). If the in-flight fetch was asked to cancel, it creates a superseding run that awaits the old one before it calls the handler (`:289`, `:313`). A new worker, if one were wrongly created, would call the handler synchronously inside `QueryAsync`.
- **S3. Freshness and invalidation are decided synchronously.** `QueryAsync` checks `IsStale` before it runs (`RevalClient.cs:239-243`). `Invalidate` goes to `NotifyInvalidated`, then `OnInvalidated`, then `HandleInvalidation`, then `RunIfStaleNow`, then `RunAsync`, all inline (`QueryState.cs:484-489`, `QueryWorker.cs:232-263`).
- **S4. The cancelled-result drop comes before the Idle notification.** After the handler returns, `fetchCts.Token.ThrowIfCancellationRequested()` (`QueryWorker.cs:366`) runs before `ApplyFetched` (`:368`). `FetchStatus = Idle` and `NotifyChanged` happen in `finally` (`:385-386`). `completion` is set after that (`:323`), and a cancelled fetch does not save (`:389`). So once a cancelled fetch reports Idle, or its in-flight task completes, nothing later writes Data. That makes "the dropped result never lands" a **synchronous consequence of an awaitable signal**, not a negative that has to be waited out.
- **S5. `CancelAsync` waits for the in-flight run.** It snapshots `_inFlight` and awaits it (`QueryWorker.cs:168-184`, `RevalClient.cs:158-171`). Its Task is therefore a deterministic "this fetch has unwound" barrier.
- **S6. Real retry backoff.** `RetryOptionsBuilder.Retry(n)` without a delay keeps `RetryDelay` null, so `CoreRetryOptions.Apply` falls back to `DefaultDelayCalculator` = `min(1000·2^attempt, 30000)` ms (`RetryOptions.cs:35-36, 52-59, 145-150`): 2 s, 4 s, 8 s, …. `Task.Delay(TimeSpan.Zero, ct)` returns a completed task synchronously, so a zero-delay retry adds no timer and no yield.
- **S7. Staleness uses a strict `>` against the real clock.** `QueryState.IsStale` returns `DateTimeOffset.UtcNow - _lastUpdatedAt > StaleTime` (`QueryState.cs:293`). See Finding F1.

## TestKit additions these files need (decisions for whoever writes the TestKit)

- **`GatedHandler<T>` needs a token-ignoring mode** (e.g. `new GatedHandler<string>(honourToken: false)`). Five tests here exist *because* the handler ignores its token: CancellationTests `A_Handler_That_Ignored_Its_Token…`, `CancelAsync_Completes_Only_Once…`, `CancelAsync_Cancels_Every_Query_Under_The_Prefix`, `A_Handler_Failing_While_Cancelled…`, `A_Fetch_Requested_After_A_Cancel…`, plus OrphanedFetchTests `CancelAsync_Reaches_A_Fetch…`. A token-honouring handler throws `OperationCanceledException` the moment the test cancels. That skips the code path under test (S4's drop, the IOException-while-cancelled filter at `QueryWorker.cs:379-382`, the superseded run) and makes those tests vacuous.
- **`GatedHandler<T>.Token(i)`**: the `CancellationToken` that call *i* received, so a test can assert synchronously whether a call was cancelled.
- **`TestUtils.Discard`** is sound (it swallows the exception and nothing else) and moves to the TestKit unchanged.

---

## RevalQuery.Core.Tests/TestUtils.cs

Shared helpers, not tests.

- `WaitUntilAsync` (`:7-11`): polls every 10 ms, then **returns silently** at the deadline (P5). Delete it. Every caller in this group gets a signal instead (entries below). The other callers are listed in the research doc.
- `Discard` (`:13-27`): keep, move to TestKit.
- `WaitForStateAsync` (`:29-46`): the research doc's two defects hold, at slightly different lines than it gives (it says 27-43):
  - The predicate is checked at `:31` before the subscribe at `:36`, with no re-check after subscribing, so a lost wakeup is possible.
  - `new TaskCompletionSource<bool>()` at `:33` has no `RunContinuationsAsynchronously`.

  It also takes `IQueryState`, so it cannot serve `MutationState`. Replace it with `Signals.UntilAsync`. **In this group every one of its 13 calls is either vacuous** (the predicate is already true when it is called, by S1) **or waits on a predicate that was true before the awaited change** (`QueryAsyncIntegrationTests.cs:63`). None of them is a real wait today.

## RevalQuery.Core.Tests/CancellationTests.cs

File-level: no statics. `NewClient()` (`:265-266`) is fine. Every handler uses raw, non-`RunContinuationsAsynchronously` TCSs or `Task.Delay`. Move all of them to `Gate`/`GatedHandler`. After the change, every `release` must be opened in a `finally` (or the Gate disposed), so that a failed assertion does not leave a handler parked forever.

### `A_Handler_That_Ignored_Its_Token_Has_Its_Result_Dropped` (`CancellationTests.cs:14`)
- **Today:** the handler ignores its token and runs `Task.Delay(200)` (`:23`). The test cancels, sets `Data = "optimistic"`, then polls silently for `IsIdle` (`:33`), then sleeps 100 ms "to be sure" (`:35`).
- **Pattern:** P3 (the 200 ms timer is the fetch's end) + P5 (`:33`) + P2 (`:35`).
- **Fix:** `GatedHandler<string>(honourToken: false)`.
  1. `Subscribe`. Then `Assert.Equal(1, handler.Calls)`: the start is synchronous (S1), so no wait is needed.
  2. `observer.Query.Cancel()`, then `observer.Query.Data = "optimistic"`.
  3. `Assert.True(observer.Query.IsFetching)`. This proves the handler really is still running past the cancel.
  4. `handler.Release(0, "from-network")`.
  5. `await client.CancelAsync("ignored").WaitAsync(HangGuard)` as the unwind barrier (S5). `Signals.UntilAsync(observer.Query, q => q.IsIdle)` is equivalent, because Idle is only reached after step 4.
  6. `Assert.Equal("optimistic", Data)`, `Assert.False(IsException)`.

  Delete the 100 ms sleep. By S4 the drop decision is taken before Idle/completion, and no later write path exists.
- **Phase:** A.
- **Risk:** the handler must ignore its token, or the test no longer reaches `QueryWorker.cs:366`. Step 3 is new and is what keeps the test honest. Without it, a library that ignored `Cancel()` but finished fast would still pass, because the optimistic write happens after Idle.

### `CancelAsync_Completes_Only_Once_The_Fetch_Has_Unwound` (`:42`)
- **Today:** the handler ignores its token and runs `Task.Delay(200)` (`:51`). The test awaits `CancelAsync`, then asserts Idle and null Data. The 200 ms is what makes "waits for the unwind" observable.
- **Pattern:** P3.
- **Fix:** `GatedHandler<string>(honourToken: false)`.
  1. Subscribe. `Assert.Equal(1, Calls)`.
  2. `var cancelling = client.CancelAsync("unwind")`.
  3. `Assert.False(cancelling.IsCompleted)` and `Assert.True(observer.Query.IsFetching)`. This is deterministic: the handler cannot finish while its gate is shut. It is the assertion the test name promises.
  4. `Release(0, "from-network")`.
  5. `await cancelling.WaitAsync(HangGuard)`.
  6. `Assert.True(IsIdle)`, `Assert.Null(Data)`.
- **Phase:** A.
- **Risk:** today the property is only shown indirectly: if `CancelAsync` did not wait, `IsIdle` would be false. Step 3 makes it direct. The token must be ignored, as in the previous test.

### `CancelAsync_Cancels_Every_Query_Under_The_Prefix` (`:65`)
- **Today:** a `CountdownEvent` is waited on synchronously with a 2 s guard (`:82`, blocking the test thread). Both handlers ignore their token and run `Task.Delay(200)` (`:75`).
- **Pattern:** P3 + P8.
- **Fix:** two `GatedHandler<string>(honourToken: false)`, one per key.
  1. Subscribe both. `Assert.Equal(1, a.Calls)`, `Assert.Equal(1, b.Calls)`, synchronously (S1). Delete the `CountdownEvent`.
  2. `var cancelling = client.CancelAsync(ValueTuple.Create("users"))`.
  3. `Assert.False(cancelling.IsCompleted)`.
  4. `a.Release(0, "from-network")`, `b.Release(0, "from-network")`.
  5. `await cancelling.WaitAsync(HangGuard)`.
  6. Both queries are Idle with null Data.

  The null Data after release is what proves each fetch was cancelled (S4). A fetch the prefix missed would land "from-network".
- **Phase:** A.
- **Risk:** with token-honouring handlers the null-Data check would prove only that `CancelAsync` returned. Keep the handlers token-ignoring.

### `A_Cancelled_Await_Abandons_The_Wait_And_Keeps_The_Fetch` (`:93`)
- **Today:** the handler sleeps 200 ms (`:102`). The test polls silently for Data (`:114`), then asserts.
- **Pattern:** P3 + P5.
- **Fix:** `GatedHandler<string>`. Either mode works, because the fetch token is never cancelled here.
  1. `var fetch = QueryAsync(options, cts.Token)`. `Assert.Equal(1, Calls)`.
  2. `await cts.CancelAsync()`.
  3. `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch)`. It completes via `WaitAsync(ct)` at `RevalClient.cs:245`, independent of the handler.
  4. `var state = client.FindQuery<string>("abandoned")!`, then `Assert.True(state.IsFetching)`. This is new: it proves the fetch was kept, not just that it later produced data.
  5. `Release(0, "from-network")`.
  6. `await Signals.UntilAsync(state, s => s.IsResolved)`.
  7. `Assert.Equal("from-network", state.Data)`.
- **Phase:** A.
- **Risk:** none. The state survives: default GcTime is 5 min (`CacheOptions.cs:9-11`) and nothing evicts in between.

### `A_Cancelled_Fetch_Does_Not_Throw_An_Older_Fetch_Failure` (`:119`)
- **Today:** call 1 throws. Call 2 is held by `Task.Delay(5000, token)` (`:131`), a timer used as a hold. It is deterministic unless the test stalls for 5 s, in which case call 2 resolves and the test fails for the wrong reason.
- **Pattern:** P3 (the hold is a timer).
- **Fix:** `GatedHandler<string>` (token-honouring).
  1. `var first = QueryAsync`. `handler.Fail(0, new InvalidOperationException("first"))`.
  2. `await Assert.ThrowsAsync<InvalidOperationException>(() => first)`. `Assert.True(IsException)`.
  3. `var second = QueryAsync`. `Assert.Equal(2, Calls)`, synchronously.
  4. `await client.CancelAsync("stale-error").WaitAsync(HangGuard)`.
  5. `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second)`.

  Never release call 2. The cancelled token unwinds it.
- **Phase:** A.
- **Risk:** `Fail(0, …)` faults asynchronously (RunContinuationsAsynchronously), so step 2 must await `first`; it cannot assert synchronously. The current `calls == 1` synchronous throw makes `first` complete inline; either form is fine.

### `Unsubscribing_Does_Not_Cancel_A_Fetch_Someone_Else_Is_Awaiting` (`:147`)
- **Today:** gated start and release with 5 s guards (`:169`, `:175`). It is deterministic. The raw TCS runs the whole fetch completion inline inside `release.TrySetResult()` on the test thread (harmless).
- **Pattern:** P8 (ok).
- **Fix:** `GatedHandler<string>` (token-honouring).
  1. Subscribe the disabled observer. `Assert.Equal(0, Calls)`.
  2. `var fetch = QueryAsync`. `Assert.Equal(1, Calls)`.
  3. `observer.Dispose()`.
  4. **New:** `Assert.False(handler.Token(0).IsCancellationRequested)`. `Dispose` is synchronous, so this is exact.
  5. `Release(0, "from-network")`.
  6. `Assert.Equal("from-network", await fetch.WaitAsync(HangGuard))`.
- **Phase:** A.
- **Risk:** with a token-honouring handler, a regression fails at step 4 instead of as an OCE from `fetch`. Both are loud.

### `A_Handler_Failing_While_Cancelled_Records_No_Error` (`:179`)
- **Today:** gated (raw TCS), 5 s guards (`:195`, `:199`). Deterministic.
- **Pattern:** P8 (ok).
- **Fix:** `GatedHandler<string>(honourToken: false)`.
  1. `var fetch = QueryAsync`.
  2. `var cancelling = CancelAsync("aborted")`.
  3. `Assert.False(cancelling.IsCompleted)`.
  4. `handler.Fail(0, new IOException("socket aborted"))`.
  5. `await cancelling.WaitAsync(HangGuard)`.
  6. `Assert.False(IsException)`, `Assert.Null(Exception)`.
  7. `ThrowsAny<OperationCanceledException>(fetch)`.
- **Phase:** A.
- **Risk:** the handler **must ignore the token**. If it honours it, it throws OCE at cancel and the IOException-under-cancellation branch (`QueryWorker.cs:379-382`) is never exercised, so the test becomes vacuous.

### `A_Fetch_Requested_After_A_Cancel_Does_Not_Inherit_It` (`:209`)
- **Today:** call 1 is gated (raw TCS) and call 2 returns immediately. 5 s guards (`:229`, `:237`, `:239`). Deterministic.
- **Pattern:** P8 (ok).
- **Fix:** `GatedHandler<string>(honourToken: false)`.
  1. `var first = QueryAsync`.
  2. `var cancelling = CancelAsync("superseded")`.
  3. `var second = QueryAsync`.
  4. `Assert.Equal(1, Calls)` and `Assert.False(second.IsCompleted)`. The superseding run waits for the first (S2, `QueryWorker.cs:313`), which is deterministic while call 1 is held.
  5. `Release(0, "stale")`.
  6. `await ThrowsAny<OperationCanceledException>(first)`, then `await cancelling.WaitAsync(HangGuard)`.
  7. `await handler.Started(1).WaitAsync(HangGuard)`.
  8. `Assert.False(handler.Token(1).IsCancellationRequested)`. This is the "does not inherit" property, stated directly.
  9. `Release(1, "from-network")`. `Assert.Equal("from-network", await second.WaitAsync(HangGuard))`.
- **Phase:** A.
- **Risk:** call 1 must ignore its token. If it honours it, the cancel may unwind it before step 3, `second` becomes an ordinary fresh run, and the supersede path is never exercised.

### `Disposing_The_Client_Cancels_The_Fetches_It_Was_Driving` (`:243`)
- **Today:** `Task.Delay(5000, token)` as a hold (`:252`). Guards of 5 s (`:257`, `:262`). In a regression, the 5 s delay and the 5 s guard race each other. Either way the test fails, but for an arbitrary reason.
- **Pattern:** P3 (timer as a hold) + P8.
- **Fix:** `GatedHandler<string>` (token-honouring), never released.
  1. `var fetch = QueryAsync`. `Assert.Equal(1, Calls)`.
  2. `client.Dispose()`.
  3. **New:** `Assert.True(handler.Token(0).IsCancellationRequested)`, synchronously, because `Dispose` calls `CancelCurrentFetch` inline (`RevalClient.cs:534-538`).
  4. `await ThrowsAny<OperationCanceledException>(() => fetch.WaitAsync(HangGuard))`.
- **Phase:** A.
- **Risk:** none.

## RevalQuery.Core.Tests/HandlerCancellationTests.cs

File-level: the private `Signal()` (`:14`) becomes `Gate`. No statics.

### `A_Query_Handler_Throwing_TaskCanceledException_Is_An_Error` (`HandlerCancellationTests.cs:20`)
- **Today:** deterministic, but `ConfigureRetry(retry => retry.Retry(2))` (`:29`) keeps the default delay (S6), so the test **sleeps a real 2 s + 4 s ≈ 6 s** in `ExponentialBackoffRetryPolicy.cs:37` before it can assert.
- **Pattern:** P4 (real backoff on the path; no race, just wall time).
- **Fix:** `retry.Retry(2, _ => TimeSpan.Zero)`. `Task.Delay(Zero)` is synchronous (S6), so `QueryAsync` completes synchronously and every assertion stays as is (`IsException`, `!IsFetching`, `calls == 3`). This removes the timer; it does not shorten one. The test is about classification and retry count, not the delay. Optional phase-B companion: keep the default delay under `FakeTimeProvider` and assert `Calls == 1`, then `Advance(2 s)`, then `Calls == 2`, then `Advance(4 s)`, then `Calls == 3`.
- **Phase:** A (B optional for delay coverage).
- **Risk:** maintainer rule check. This is not "a shorter delay as a fix": the outcome never depended on the delay, and zero takes the timer off the path entirely.

### `A_Cancelled_Query_Is_Not_An_Error` (`:40`)
- **Today:** the hold is `Task.Delay(Timeout.Infinite, token)` (`:48`), which is cancellation-only and never fires. `started` is already complete when awaited (S1). Deterministic.
- **Pattern:** ok.
- **Fix:** vocabulary only. `GatedHandler<string>` (token-honouring), never released. Replace `await started.Task` with `Assert.Equal(1, Calls)` (S1). Then `await client.CancelAsync("genuine").WaitAsync(HangGuard)` and the same three assertions.
- **Phase:** A.
- **Risk:** none.

### `A_Mutation_Handler_Throwing_TaskCanceledException_Is_An_Error` (`:63`)
- **Today:** deterministic, no change. The handler is `Task.FromException`, `Retry(0)`, and the test awaits `ExecuteAsync`.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `A_Mutation_Cancelled_By_The_Callers_Token_Restores_The_Status_And_Throws` (`:86`)
- **Today:** deterministic. `Task.Delay(Infinite, token)` is the hold (`:97`). The test awaits `run`.
- **Pattern:** ok.
- **Fix:** optional `GatedHandler<string>` for vocabulary. Tighten `Assert.True(notifications > before)` (`:116`) to `Assert.Equal(before + 1, notifications)`: the cancel path notifies exactly once, in `finally` (`MutationState.cs:253`), and the cancel branch makes no other `NotifyChanged` call (`:191-201`).
- **Phase:** A.
- **Risk:** the exact count also pins "no extra notification". That is the intended contract, but confirm it with the maintainer before tightening.

### `Cancelling_The_Latest_Run_While_An_Older_One_Is_In_Flight_Does_Not_Stay_Fetching` (`:120`)
- **Today:** deterministic. The older run is gated by a TCS, the latest by `Delay(Infinite, token)` (`:131`). The test awaits both.
- **Pattern:** ok.
- **Fix:** optional `GatedHandler`. Consider replacing `Assert.NotEqual(Fetching, …)` (`:149`) with `Assert.Equal(MutationStatus.Idle, …)`: by `MutationState.cs:196-199` the latest cancel restores `_settledStatus` (Idle), and the older run, not being latest, writes nothing (`:176-181`).
- **Phase:** A.
- **Risk:** the status is already Idle *before* the older run finishes, while it is still in flight. That is a semantic choice, not a timing issue (see Findings F4).

## RevalQuery.Core.Tests/OrphanedFetchTests.cs

File-level: `InterlockedMax` helper (`:73-81`) can go once `GatedHandler.Calls` carries the count (no call can start while another is held without showing up in `Calls`). No statics.

### `CancelAsync_Reaches_A_Fetch_Left_Running_By_A_Released_Worker` (`OrphanedFetchTests.cs:11`)
- **Today:** gated (raw TCS), 5 s guards (`:26`, `:32`). Deterministic. The handler ignores its token, which is what lets null Data prove the cancel reached the fetch.
- **Pattern:** P8 (ok).
- **Fix:** `GatedHandler<string>(honourToken: false)`.
  1. Subscribe. `Assert.Equal(1, Calls)`.
  2. `observer.Dispose()`.
  3. `var cancelling = CancelAsync("orphan")`.
  4. `Assert.False(cancelling.IsCompleted)`. This proves `CancelAsync` found the worker still held by `_releaseWhenSettled` (`QueryWorker.cs:147-157`).
  5. `Release(0, "from-network")`.
  6. `await cancelling.WaitAsync(HangGuard)`.
  7. `Assert.Null(Data)`.
- **Phase:** A.
- **Risk:** token must be ignored.

### `A_Released_Worker_Does_Not_Let_A_Second_Fetch_Run_Alongside_Its_Own` (`:38`)
- **Today:** after `observer.Dispose()` and `QueryAsync`, the test sleeps 100 ms (`:65`) to give a hypothetical second fetch time to start. It then asserts `peak == 1`.
- **Pattern:** P2. The research doc does not list this sleep.
- **Fix:** `GatedHandler<string>`.
  1. Subscribe. `Assert.Equal(1, Calls)`.
  2. `observer.Dispose()`.
  3. `var fetch = QueryAsync(options)`.
  4. **Immediately** `Assert.Equal(1, handler.Calls)` and `Assert.False(fetch.IsCompleted)`. By S2 a second fetch, through either the old worker or a wrongly new one, would call the handler synchronously inside `QueryAsync`, so the absence is decided before `QueryAsync` returns.
  5. `Release(0, "from-network")`. `await fetch.WaitAsync(HangGuard)`.
  6. `Assert.Equal(1, Calls)`.

  Delete the sleep and `InterlockedMax`.
- **Phase:** A.
- **Risk:** this relies on S2 staying synchronous. If the library ever moved the handler call behind a yield (e.g. `Task.Run`), step 4 would pass vacuously. Note it in the test's remarks so such a change revisits the test. The sleep did not cover that case either.

## RevalQuery.Core.Tests/QueryAsyncTests.cs

File-level:
- `_client` is created in the constructor (`:17-19`) and **never disposed**. The class is not `IDisposable`, so every test leaks a `TtlQueryGarbageCollector` loop (`Task.Run` + 1-minute `Task.Delay`, `TtlQueryGarbageCollector.cs:112-136`) for the rest of the run. Make the class `IDisposable` and dispose the client.
- `StaticHandler` returns the interned literal `"data"`, which makes `ReferenceEquals` meaningless (see `QueryAsync_ConcurrentCalls_SameKey`).
- Local `calls` ints become `Counter`/`GatedHandler.Calls`.

### `QueryAsync_ReturnsData` (`QueryAsyncTests.cs:21`)
- **Today:** deterministic, no change. The handler is synchronous (S1).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `QueryAsync_ThrowsOnHandlerException` (`:31`)
- **Today:** no retry configured, so the default is 3 retries at 2/4/8 s (S6). **The test sleeps ≈ 14 s of real backoff** before the exception surfaces. Deterministic but slow, and the slowest test in this group by far.
- **Pattern:** P4 (real backoff, no race).
- **Fix:** `.ConfigureRetry(r => r.Retry(3, _ => TimeSpan.Zero))` with a `Counter` in the handler. Then `await Assert.ThrowsAsync<InvalidOperationException>(...)` and `Assert.Equal(4, counter.Value)`. This keeps the "after the default number of attempts" meaning while taking the timer off the path. Optional B: default delay under `FakeTimeProvider` with `Advance(2 s)`, `Advance(4 s)`, `Advance(8 s)`, asserting the count after each.
- **Phase:** A (B optional).
- **Risk:** the default-delay *calculator* remains untested anywhere in this group (see F3).

### `QueryAsync_ConcurrentCalls_SameKey` (`:39`)
- **Today:** **cannot fail.** The handler returns the interned literal `"data"`, so `ReferenceEquals` is true for any three results. The calls are also not concurrent: by S1 each `QueryAsync` completes before the next one starts, so no join ever happens.
- **Pattern:** P9.
- **Fix:** `GatedHandler<object>` (or `string` with a non-interned value).
  1. `var t1 = QueryAsync`, `var t2 = QueryAsync`, `var t3 = QueryAsync`.
  2. `Assert.Equal(1, handler.Calls)`, synchronously (S2).
  3. `var value = new object()`, or `new string('d', 4)`, then `handler.Release(0, value)`.
  4. `var results = await Task.WhenAll(t1, t2, t3).WaitAsync(HangGuard)`.
  5. `Assert.All(results, r => Assert.Same(value, r))`.
  6. `Assert.Equal(1, handler.Calls)`.
- **Phase:** A.
- **Risk:** `Key` here is the same as in other tests of the class, but each test has its own client, so there is no cross-talk.

### `QueryAsync_ServesFreshDataFromCacheWithoutCallingTheHandler` (`:54`)
- **Today:** two synchronous calls with a 5 min StaleTime. It reads the real clock (S7), with a 5 min margin. Deterministic in practice.
- **Pattern:** P4 (margin only).
- **Fix:** phase B: build the client with `RevalQueryOptions.TimeProvider = FakeTimeProvider`. With no `Advance`, the age is exactly zero, so freshness is exact. Optionally add the boundary under fake time: `Advance(5 min)` refetches (once F1 is fixed: age == StaleTime is stale in TanStack).
- **Phase:** B (no change needed in A).
- **Risk:** none today.

### `QueryAsync_RefetchesWhenTheDataIsStale` (`:70`)
- **Today:** two back-to-back synchronous calls with the default `StaleTime = 0`. The second call refetches only if `UtcNow` moved between them, because the test is `>` (S7). It is clock-resolution-dependent: if both calls land in one clock tick, `calls == 1` and the test fails. This is rare on Linux but real.
- **Pattern:** P4. It is also the symptom of library bug F1.
- **Fix:** fix F1 (`>=`, TanStack parity). StaleTime 0 is then stale at age 0, and the test is deterministic **with no fake time and no test change**. Under phase B without F1, a frozen `FakeTimeProvider` makes this test **fail deterministically**.
- **Phase:** A once F1 is fixed (library). Otherwise blocked; B alone breaks it.
- **Risk:** F1 also affects every StaleTime-zero refetch test in other groups once fake time arrives.

### `QueryAsync_JudgesStalenessByTheOptionsThisCallPassed` (`:84`)
- **Today:** synchronous. The second call uses StaleTime 5 min against a just-set timestamp, on the real clock with a large margin. Deterministic in practice.
- **Pattern:** P4 (margin only).
- **Fix:** none in A. Under B the frozen clock makes it exact.
- **Phase:** B (no change needed in A).
- **Risk:** none.

### `QueryAsync_WithNeverStale_ServesCacheEvenAfterInvalidation` (`:105`)
- **Today:** deterministic, no change. `NeverStale` makes `Static` true, and `IsStale` returns false before it reads the clock (`QueryState.cs:290`). `Invalidate` with no observer is synchronous (S3).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `QueryAsync_WithALongStaleTime_StillRefetchesAfterInvalidation` (`:122`)
- **Today:** deterministic, no change. `_isInvalidated` returns true before the clock is compared (`QueryState.cs:291`).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `QueryAsync_ServesFreshDataHeldBesideAnOlderFailure` (`:139`)
- **Today:** call 2 (`:154`, default StaleTime 0) must refetch and throw. It refetches only if the clock ticked since call 1 (S7). In the same tick it returns cached `"data"` and `ThrowsAsync` fails.
- **Pattern:** P4. Symptom of F1.
- **Fix:** as for `QueryAsync_RefetchesWhenTheDataIsStale`: deterministic once F1 is fixed. Call 3 (5 min StaleTime) is fine.
- **Phase:** A once F1 is fixed (library). B alone makes it fail deterministically.
- **Risk:** this test also proves that a failure does not move `LastUpdatedAt` (`ApplyFailed`, `QueryState.cs:325-333`). Keep that.

## RevalQuery.Core.Tests/QueryAsyncDiscardedTests.cs

File-level:
- `_client` is never disposed (`:17-20`). Same GC-loop leak as `QueryAsyncTests`: make the class `IDisposable`.
- Synchronous handlers make "discarded" meaningless: the Task is already complete when `Discard` sees it (S1). Gated handlers give the discard something to abandon.

### `A_Discarded_Call_Still_Stores_Its_Data_In_The_Registry` (`QueryAsyncDiscardedTests.cs:23`)
- **Today:** `WaitForStateAsync` (`:28`) returns on its first check, because the state is already Resolved (S1).
- **Pattern:** P1a + P5 (helper).
- **Fix:** `GatedHandler<string>`.
  1. `TestUtils.Discard(QueryAsync)`.
  2. `var state = FindQuery(Key)!`. `Assert.True(state.IsFetching)`.
  3. `Release(0, "data")`.
  4. `await Signals.UntilAsync(state, s => s.IsResolved)`.
  5. `Assert.Equal("data", ((IQueryState<string>)state).Data)`.
- **Phase:** A.
- **Risk:** none.

### `A_Second_Discarded_Call_Reuses_Fresh_Data_Rather_Than_Refetching` (`:36`)
- **Today:** the first wait is vacuous (`:46`). After the second `Discard` the test sleeps 50 ms (`:51`) to show the absence of a refetch.
- **Pattern:** P2 (the research doc does not list this one) + P5.
- **Fix:** keep the synchronous handler with a `Counter`.
  1. `Discard(QueryAsync)`. `Assert.True(IsResolved)` and `Assert.Equal(1, counter)`, synchronously (S1).
  2. `Discard(QueryAsync)`.
  3. **Immediately** `Assert.Equal(1, counter)` and Data unchanged. The freshness decision is synchronous (S3, `RevalClient.cs:239-243`), and a refetch would call the handler synchronously (S1).

  Delete the sleep.
- **Phase:** A (B makes the 5 min margin exact).
- **Risk:** as with OrphanedFetch, this relies on S1/S3 staying synchronous.

### `A_Discarded_Call_Leaves_A_Handler_Failure_On_The_Query` (`:59`)
- **Today:** `Record.Exception` proves `Discard` does not throw. The wait (`:68`) is vacuous (S1).
- **Pattern:** P1a + P5.
- **Fix:** `GatedHandler<string>`, `Retry(0)`.
  1. `Assert.Null(Record.Exception(() => Discard(QueryAsync)))`.
  2. `handler.Fail(0, new InvalidOperationException("fail"))`.
  3. `await Signals.UntilAsync(state, s => s.IsException)`.
  4. `Assert.IsType<InvalidOperationException>(state.Exception)`.

  A synchronous version without gates is also fine: assert `IsException` straight after `Discard`.
- **Phase:** A.
- **Risk:** keep `Record.Exception`. Today it is trivially satisfied, because `QueryAsync` is `async` and never throws synchronously.

### `The_Same_Call_Awaited_Throws_Instead` (`:75`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `Discarded_Calls_On_Different_Keys_Are_Independent` (`:85`)
- **Today:** both waits (`:90-91`) are vacuous (S1). Independence is never really exercised, because each call finishes before the next starts.
- **Pattern:** P1a + P5. Weak (P9-adjacent).
- **Fix:** two `GatedHandler<string>`.
  1. Discard both.
  2. `b.Release(0, "data2")` first. `await Signals.UntilAsync(state2, s => s.IsResolved)`.
  3. `Assert.True(state1.IsFetching)`. Key 1 is unaffected by key 2 settling.
  4. `a.Release(0, "data")`. `await Signals.UntilAsync(state1, s => s.IsResolved)`.
  5. Assert both Data values.
- **Phase:** A.
- **Risk:** none.

## RevalQuery.Core.Tests/QueryAsyncIntegrationTests.cs

File-level:
- `_client` is never disposed (`:15-18`), the same leak.
- `StaticDataHandler` uses `DateTime.UtcNow.Ticks` (`:84`, P7). Replace it with a `Counter` or a `GatedHandler`.

### `SubscribeAfterDiscardedQueryAsync_UsesCachedData` (`QueryAsyncIntegrationTests.cs:21`)
- **Today:** the wait is vacuous (`:26`, S1). On `Subscribe` the default StaleTime 0 usually triggers a synchronous refetch with the same handler, which returns the same `"cached"`. So the assertion `Data == "cached"` **cannot tell cache from refetch**, and the test cannot fail on the property its name states.
- **Pattern:** P1a + P9.
- **Fix:** `GatedHandler<string>`.
  1. `Discard(QueryAsync)`. `Release(0, "cached")`.
  2. `await Signals.UntilAsync(state, s => s.IsResolved)`.
  3. `var observer = Subscribe(...)`.
  4. **Synchronously** `Assert.Equal("cached", observer.Query.Data)` and `Assert.True(IsResolved)`. Any refetch is parked at gate 1, so Data can only have come from the cache.
  5. Then (requires F1) `Assert.Equal(2, Calls)` and `Assert.True(IsFetching)`: stale-while-revalidate, as TanStack does on mount with staleTime 0.
  6. `Release(1, "fresh")` and `await Signals.UntilAsync(observer.Query, q => q.IsIdle)` so the test ends quiescent.
- **Phase:** A. Step 5 waits on F1; without F1, step 5 is clock-dependent, so omit it until F1 is fixed.
- **Risk:** decide whether the test's intent is "served from cache while it refetches" (above) or "no refetch at all". For the latter, set StaleTime 5 min and assert `Calls == 1`.

### `SubscribeAfterQueryAsync_UsesCachedData` (`:35`)
- **Today:** the same as the previous test, with an awaited `QueryAsync`. The wait at `:41` is vacuous, and it has the same cache-vs-refetch indistinguishability.
- **Pattern:** P1a + P9.
- **Fix:** the same steps as `SubscribeAfterDiscardedQueryAsync_UsesCachedData`, starting with `var v = QueryAsync; Release(0, "fetched"); await v.WaitAsync(HangGuard)`. Otherwise the two tests are duplicates; let the difference be "awaited" vs "discarded".
- **Phase:** A (step 5 waits on F1).
- **Risk:** as above.

### `InvalidateAfterQueryAsync_TriggersRefetch` (`:48`)
- **Today:** the waits at `:55` and `:58` are vacuous (S1). The wait at `:63` checks `IsResolved`, **which is already true before the refetch**, so it waits on nothing. The test passes only because the refetch is synchronous (S3) and `$"v{Ticks}"` allocates a new string on every call, so `NotSame` amounts to "the handler ran again".
- **Pattern:** P5 (a wait on a predicate already true) + P7 (Ticks).
- **Fix:** `GatedHandler<string>`, StaleTime 30 s kept.
  1. `Discard(QueryAsync)`. `Release(0, "v1")`. `await Signals.UntilAsync(state, s => s.IsResolved)`.
  2. Subscribe. `Assert.Equal(1, Calls)` and `Assert.Equal("v1", Data)`: fresh, no refetch.
  3. `_client.Invalidate(Key)`.
  4. **Synchronously** `Assert.Equal(2, Calls)` and `Assert.True(observer.Query.IsFetching)` (S3).
  5. `Release(1, "v2")`.
  6. `await Signals.UntilAsync(observer.Query, q => q.IsIdle)`.
  7. `Assert.Equal("v2", observer.Query.Data)`.
- **Phase:** A (B makes the 30 s margin in step 2 exact).
- **Risk:** none.

### `QueryAsync_WithEnabledFalse_StillFetches` (`:69`)
- **Today:** the wait at `:76` is vacuous (S1).
- **Pattern:** P1a + P5.
- **Fix:** a synchronous handler with a `Counter`. `Discard(QueryAsync)`, then straight away `Assert.Equal(1, counter)` and `Assert.True(FindQuery(Key)!.IsResolved)`.
- **Phase:** A.
- **Risk:** none.

## RevalQuery.Core.Tests/RetryCountTests.cs

File-level: no statics, no client. `Retries(count)` (`:8-9`) uses a zero delay, so every test runs synchronously (S6).

### `A_Failing_Handler_Is_Called_Retry_Plus_One_Times` (`RetryCountTests.cs:15`, Theory)
- **Today:** deterministic, no change (zero delay, synchronous).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `Zero_Retries_Surfaces_The_Handler_Exception_Rather_Than_Inventing_One` (`:31`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `A_Handler_That_Recovers_Stops_Retrying` (`:43`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

### `Cancellation_Stops_Retrying_And_Surfaces_The_Failure` (`:61`)
- **Today:** deterministic, but **weak**. `Assert.ThrowsAnyAsync<Exception>` (`:67`) accepts any exception, so the "surfaces the failure" half of the name is never checked. A policy that went on to `Task.Delay(0, cancelledToken)` and threw `TaskCanceledException` would still need `calls == 1` to fail the test. A policy that threw OCE *instead of* the handler's exception would pass.
- **Pattern:** P9 (half-vacuous).
- **Fix:** capture `var thrown = new InvalidOperationException("boom")`, then `var caught = await Assert.ThrowsAsync<InvalidOperationException>(...)`, `Assert.Same(thrown, caught)` and `Assert.Equal(1, calls)`. Under the current code, the filter at `ExponentialBackoffRetryPolicy.cs:44` is false once the token is cancelled, so the handler's own exception propagates.
- **Phase:** A.
- **Risk:** this pins that the handler's exception surfaces, not an OCE. That matches `QueryWorker.cs:379-382`, which then treats it as cancelled anyway.

### `Queries_Default_To_Three_Retries_And_Mutations_To_None` (`:79`)
- **Today:** deterministic, no change (synchronous).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none needed.
- **Risk:** none.

---

## Tally (39 methods)

- **Phase A, test-only change:** 26. This includes the two backoff tests, whose zero-delay fix is A with an optional B.
- **Phase A, but blocked on library fix F1:** 2 (`QueryAsync_RefetchesWhenTheDataIsStale`, `QueryAsync_ServesFreshDataHeldBesideAnOlderFailure`), plus step 5 of the two `UsesCachedData` tests.
- **Phase B only (fine today on a margin; exact under fake time):** 2 (`ServesFreshDataFromCache…`, `JudgesStalenessByTheOptions…`).
- **No change:** 9 (HandlerCancellation `A_Mutation_Handler_Throwing…`; QueryAsyncTests `ReturnsData`, `WithNeverStale…`, `WithALongStaleTime…`; Discarded `The_Same_Call_Awaited…`; RetryCount Theory, `Zero_Retries…`, `Recovers…`, `Defaults…`), plus vocabulary-only touches elsewhere.
- **NEEDS-SIGNAL:** none. Every wait in this group maps onto an existing signal: `CancelAsync`'s Task, the `QueryAsync`/`ExecuteAsync` Tasks, `OnChanged`, or synchronous decisions (S1-S5).

## Findings

**Library bugs (suspected)**
- **F1. `IsStale` is off by one at the boundary (`QueryState.cs:293`).** `UtcNow - _lastUpdatedAt > StaleTime` treats age == StaleTime as fresh. TanStack's `isStaleByTime` returns `!timeUntilStale(updatedAt, staleTime)`, with `timeUntilStale = max(updatedAt + staleTime - Date.now(), 0)` (`query.ts`, `utils.ts` at `ee47993`, fetched). So in TanStack, age >= staleTime is stale, and staleTime 0 is always stale. RevalQuery's own doc says the default is "always stale" (`FetchOptions.cs:13`). Consequences:
  1. With StaleTime 0, a read in the same clock tick as the fetch is fresh. That makes `QueryAsync_RefetchesWhenTheDataIsStale` and `QueryAsync_ServesFreshDataHeldBesideAnOlderFailure` clock-resolution-flaky today.
  2. **Under phase B (frozen `FakeTimeProvider`), every StaleTime-0 refetch test across the suite fails deterministically** unless this becomes `>=`. Fix F1 before or with the TimeProvider ADR.
- **F2 (not a bug, a divergence worth knowing).** A cancelled fetch whose handler ignores the token stays `Fetching` until the handler returns, and `CancelAsync` waits for it (`QueryWorker.cs:168-184`, `:366`). TanStack's retryer rejects at once with `CancelledError` and the state reverts immediately. RevalQuery's behaviour is deliberate (pinned by `CancelAsync_Completes_Only_Once_The_Fetch_Has_Unwound`). It is listed only because five tests depend on it.
- **F4 (semantic question, not timing).** In `Cancelling_The_Latest_Run_While_An_Older_One_Is_In_Flight…`, cancelling the latest run sets the status to Idle (`MutationState.cs:196-199`) while the older run is still executing. The test asserts only `!= Fetching`. It is consistent with "status tracks the latest run", but confirm that this is the intended reading.

**Research-doc claims that are wrong or incomplete (for this group)**
- **It misses two P2 sleeps:** `OrphanedFetchTests.cs:65` (`Task.Delay(100)`) and `QueryAsyncDiscardedTests.cs:51` (`Task.Delay(50)`).
- **It misses real backoff in two tests:** `HandlerCancellationTests.cs:29` (`Retry(2)` with the default delay, ≈ 6 s) and `QueryAsyncTests.cs:31` (default 3 retries, ≈ 14 s). P4 says only that "no test can check the delays", but these tests *pay* for them on every run.
- **Its P9 bullet is misleading.** It calls `CancellationTests.cs:23` "a handler that ignores its token" as a weakness. For `:14`, `:42` and `:65` (and `:179`, `:209`, `OrphanedFetchTests.cs:11`), ignoring the token *is the scenario*. A fix that makes those handlers honour the token turns them vacuous. Only the 200 ms timer is the defect, not the ignoring.
- **`TestUtils.WaitForStateAsync` line numbers are slightly off.** It is at `:29-46` (check `:31`, TCS `:33`, subscribe `:36`), not `:27-43`. The defects it describes are real.
- **The P4 staleness section misses F1.** It says StaleTime(0) side-steps the clock. It does not: with `>`, StaleTime 0 still reads the clock and is tick-dependent.
- **The probe claim is confirmed** (S1), and it reaches further than the doc says: `QueryAsync` with a synchronous handler returns an already-completed Task. So every `Discard` + `WaitForStateAsync` in this group is vacuous.

**Tests that cannot fail, or only partly**
- `QueryAsyncTests.QueryAsync_ConcurrentCalls_SameKey`: it uses an interned literal and is never concurrent. It cannot fail.
- `QueryAsyncIntegrationTests.SubscribeAfterDiscardedQueryAsync_UsesCachedData` and `SubscribeAfterQueryAsync_UsesCachedData`: they cannot tell cache from refetch. They cannot fail on the stated property.
- `QueryAsyncIntegrationTests.InvalidateAfterQueryAsync_TriggersRefetch` `:63`: the wait is on a predicate that is already true. The test only works because the refetch is synchronous.
- `RetryCountTests.Cancellation_Stops_Retrying_And_Surfaces_The_Failure`: the "surfaces the failure" half is unchecked (`ThrowsAnyAsync<Exception>`).
- `QueryAsyncDiscardedTests.Discarded_Calls_On_Different_Keys_Are_Independent`: independence is never exercised, because the calls never overlap.

**Other**
- **F3 (coverage gap).** No test in this group exercises `DefaultDelayCalculator` or any retry delay. With the backoff tests moved to zero delay, the delay schedule is untested until phase B adds a `FakeTimeProvider` backoff test.
- **The three `QueryAsync*` classes never dispose their `RevalClient`.** Each test leaks a GC sweep loop for the whole run.
