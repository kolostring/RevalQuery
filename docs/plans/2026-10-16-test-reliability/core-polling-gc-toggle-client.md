# g3: reliability plan for IssueReproduction, EvictionRace, Polling, EnabledToggle, StaticQuery, Disposal, RevalClient tests

I checked every claim below against the working tree. Line numbers are per file. Library references are `RevalQuery.Core/...`.

## Library facts this plan relies on (verified)

- **F1. Subscribe/Invalidate/SetOptions decide synchronously.** `RevalClient.Subscribe` (`RevalClient.cs:320-347`) calls `ApplyOptions` and then `RunIfStale`. With no persistence, `TryDeferUntilRestored` returns false (`QueryState.cs:397-407`, `_restoreCompletion` is null), so `RunIfStaleNow -> RunIfAllowed -> RunAsync` runs on the caller's thread. `RunAsync` (`QueryWorker.cs:275-301`) calls `RunCoreAsync` without yielding. `RunFetchAsync` sets `Fetching`, notifies, and calls the handler through `ExponentialBackoffRetryPolicy`, whose first attempt awaits nothing (`ExponentialBackoffRetryPolicy.cs:298-311`). A handler that returns a completed task therefore reaches `ApplyFetched` and `FetchStatus = Idle` before `Subscribe` returns. `Invalidate` does the same through `NotifyInvalidated -> HandleInvalidation -> RunIfStaleNow` (`QueryWorker.cs:232-258`). `RevalHooks.Query` on an existing slot goes through `SetObserverOptions -> ApplyOptions -> RunIfStale` when `!wasEnabled` (`QueryWorker.cs:123-144`), which is also synchronous.
- **F2. Worker release happens before the `QueryAsync` Task completes.** `RunAndReleaseAsync`'s `finally { ReleaseIfUnobserved }` (`RevalClient.cs:567-574`) runs before the returned Task completes, so awaiting that Task is a deterministic "worker released" signal. *(Corrected by the parent after a probe on net10.0: with no persistence and a synchronous handler, `QueryAsync` returns an **already-completed** Task, on first call and on refetch. The completion TCS's `RunContinuationsAsynchronously` only affects continuations registered before it completes; `RunAndReleaseAsync` awaits it after, so it continues inline. So the release has also happened by the time `QueryAsync` returns. The fixes below that await the Task stay correct; the claim that release is asynchronous was wrong.)*
- **F3. Polling.** `StartPolling` returns at `QueryWorker.cs:200` when `RefetchInterval <= 0`. Otherwise it starts `Task.Run(loop { await Task.Delay(interval, token); RunIfAllowed(); })` (`:212-229`). `RunIfAllowed` requires `CanFetch = Idle && IsEnabled` (`QueryState.cs:234`). The worker constructor arms polling only `if (Query.CanFetch)` (`QueryWorker.cs:86-89`), so a worker created by `QueryAsync` (no observers, so `IsEnabled` is false) never arms a poll. `Dispose` cancels the polling CTS and makes `RunAsync` return `NotRun` (`:283, :430-447`).
- **F4. Eviction.** `TtlQueryGarbageCollector` stamps `DateTime.UtcNow + GcTime` (`:42`). It sweeps `Expiry <= DateTime.UtcNow` (`:120-129`) from a `Task.Run` loop with `Task.Delay(GcInterval)` (`:141, :151`). `GcInterval` defaults to **1 minute** (`CacheOptions.cs`, `CoreCacheOptions.Default`), so in every test that does not set `GcInterval`, the background sweep never runs during the test. `OnEvictionRequired` is a public event. The client subscribes in its constructor (`RevalClient.cs:58`), so a handler the test adds *after* resolving the client runs after `HandleEviction` (multicast invocation order). That is a deterministic "eviction applied" signal.
- **F5. Staleness uses a strict `>`.** `IsStale` returns `DateTimeOffset.UtcNow - _lastUpdatedAt > StaleTime` (`QueryState.cs:293`). With `StaleTime == 0` and an elapsed time of exactly 0, data counts as **fresh**. See Findings #1. Every test below that relies on "StaleTime zero means a later subscriber refetches" depends on the clock having ticked. That holds on a fine-grained real clock, never holds under `FakeTimeProvider` without an `Advance`, and is unverified on a coarse clock such as browser WASM.
- **F6. Mutations.** `MutationState.ExecuteAsync` (`MutationState.cs:121-255`) passes its own `settledData`/`variables` locals to `run.Current.OnSettled` for every non-cancelled run. Only `Data`/`Status` are latest-wins (`version == _currentVersion`). `MutationDefault` has 0 retries (`RetryOptions.cs:47`).

## Proposed TestKit additions arising from these files

- `ManualEviction : ICacheEvictionPolicy`: `RegisterForEviction` only records (`Registered`: list of keys), and `Evict(ITuple key)` raises `OnEvictionRequired` synchronously on the test thread. It lets a test evict at an exact point (research topic 5, alternative 2). It uses public Core API only.
- `GatedHandler<T>` needs a per-call **`IgnoreCancellation`** mode (the call awaits its release gate *without* the token). The supersede interleaving in `EvictionRaceTests` needs a fetch that has been asked to cancel but has not unwound. A handler that honours the token unwinds on a pool continuation that races the test. `GatedHandler` should also expose `Cancelled(i)`, a Task that completes when call `i`'s token fires, so a test can assert that cancellation reached the handler.
- A pre-released `GatedHandler` call (`ReleaseAll(v)` before the call) must complete synchronously, because `await` on a completed gate does not yield. This keeps the F1 property where a test wants it.
- Reflection helpers (`WorkerCount`, `AssertEveryWorkerHasItsState`) read private Core fields, so they stay in `RevalQuery.Core.Tests`, not TestKit. TestKit is public Core API only. They must **take `RevalClient._gate`** while walking (see EvictionRaceTests).

---

## RevalQuery.Core.Tests/IssueReproductionTests.cs

Shared fixtures and statics:
- `static int SlowHandlerCalls` (`:222`) and `SlowHandler` (`:224-229`, `Task.Delay(150)`): replace with a per-test `GatedHandler<string>`. Delete both.
- `private static WaitUntil` (`:348-352`): a silent polling helper (P5). Delete it.
- `Box(string Name, int DelayMs)` (`:267`): drop `DelayMs`.
- `SpinHandler` (`:231-235`) stays. It is load shape for a stress net, not a wait.
- `WorkerCount` / `EvictionSubscriberCount` (`:332-346`): reflection. Keep them, but `WorkerCount` should take `_gate` (shared helper with EvictionRaceTests).

### `Issue1_EvictionPolicy_Is_Scoped_Alongside_RevalClient` (`IssueReproductionTests.cs:21`)
- **Today:** deterministic, no change. Pure DI resolution and descriptor checks.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `Issue1_Eviction_In_One_Scope_Leaves_Another_Scope_Alone` (`:42`)
- **Today:** `WaitForStateAsync(o1/o2, IsResolved)` (`:58-59`). Under F1, both are resolved when `Subscribe` returns, so the waits return at the first check. Eviction is a manual `gc1.CollectExpiredEntries()` (`:64`) with `GcTime = 0`, compared against `DateTime.UtcNow` (F4).
- **Pattern:** P1a (plus a residual P4 clock read).
- **Fix:** delete both waits. Assert `Assert.True(o1.Query.IsResolved); Assert.True(o2.Query.IsResolved);` straight after the two `Subscribe` calls. Keep the rest unchanged: `o1.Dispose(); o2.Dispose(); gc1.CollectExpiredEntries(); Assert.Null(c1.FindQuery(k)); Assert.NotNull(c2.FindQuery(k));`.
- **Phase:** A. Under B, with `FakeTimeProvider` and no `Advance`, `Expiry == now` and `<=` still evicts, so the test becomes clock-exact for free.
- **Risk:** scope2's own background sweep could evict c2's entry (`GcTime = 0`), but only after `GcInterval` = 1 min (F4). Under B that path disappears. The `NotNull(c2…)` assertion is the actual point of the test, so keep it.

### `Issue1_Disposing_A_Scope_Detaches_Its_Client_From_The_Eviction_Policy` (`:71`)
- **Today:** `WaitForStateAsync(IsResolved)` (`:84`). It is synchronous per F1.
- **Pattern:** P1a.
- **Fix:** replace it with `Assert.True(observer.Query.IsResolved)`. The rest is synchronous: the subscriber count is 1, then `scope.Dispose()` runs `RevalClient.Dispose`, which unsubscribes at `RevalClient.cs:532`, and the count becomes 0.
- **Phase:** A.
- **Risk:** the test depends on the container disposing the client before the collector, which is reverse creation order. That is deterministic.

### `Issue2_BackgroundEviction_Runs_Without_A_Manual_Start` (`:96`)
- **Today:** `WaitForStateAsync` (`:108`, P1a), then a silent `WaitUntil(FindQuery is null, 2000)` (`:111`). Real `GcInterval` 20 ms, real `UtcNow` expiry. The assertion at `:113` repeats the predicate, so a timeout surfaces only as `Assert.Null` failing.
- **Pattern:** P4 + P5 (+ P1a).
- **Fix (A):**
  1. Resolve `client`, then `var gc = (TtlQueryGarbageCollector)scope.ServiceProvider.GetRequiredService<ICacheEvictionPolicy>()`. It is the same scoped instance, and resolving it after the client puts the test handler after `HandleEviction` in the invocation list (F4).
  2. Create `var evicted = new Gate();` and `gc.OnEvictionRequired += k => { if (QueryKeyComparer.Instance.Equals(k, ValueTuple.Create("gc-key"))) evicted.Open(); };`. Subscribe **before** `observer.Dispose()`.
  3. `Subscribe`, then `Assert.True(observer.Query.IsResolved)`, then `observer.Dispose()`.
  4. `await evicted.Task.WaitAsync(HangGuard)`, then `Assert.Null(client.FindQuery("gc-key"))`.

  The event fires only from `CollectExpiredEntries`/`EvictOldestEntries`, and only the loop calls those here. The gate opening therefore *is* the proof that the loop started without a manual start.
- **Fix (B):** `FakeTimeProvider`, with the GC sweep armed synchronously via `CreateTimer` in `EnsureStarted`. After `observer.Dispose()`, call `time.Advance(GcTime + GcInterval)` (= 20 ms) and `Assert.Null(...)` synchronously, with no gate. Also add `time.Advance(19ms)` then `Assert.NotNull(...)` first, to pin the boundary the way TanStack does (`query.test.tsx` L645-663).
- **Phase:** A, then B.
- **Risk:** in A the real 20 ms timer still drives it, but the outcome is decided by the signal, not by a deadline. `QueryKeyComparer` is internal or public? The test already uses `QueryKeyComparer.Instance` (`:288`), so it is accessible.

### `Issue2_EvictionPolicy_Needs_No_Host_To_Start` (`:117`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `Issue3_A_Discarded_QueryAsync_Releases_Its_Worker_When_The_Fetch_Settles` (`:126`)
- **Today:** `Discard(QueryAsync)`, then two silent `WaitUntil`s (`:132` resolved, `:133` `WorkerCount == 0`), then an assertion that repeats the second. `WaitForStateAsync` (`:138`) is P1a.
- **Pattern:** P5 + P1a. F2 is why the worker wait is needed at all.
- **Fix:**
  1. `var fetch = client.QueryAsync(opts); TestUtils.Discard(fetch);` and then `Assert.True(client.FindQuery("prefetched")!.IsResolved)`, synchronous per F1.
  2. `await fetch.WaitAsync(HangGuard)`. The release happens in `RunAndReleaseAsync`'s `finally` before the Task completes (F2), so this is the deterministic signal. The caller "discarding" the task does not change library behaviour, and the test only observes it.
  3. `Assert.Equal(0, WorkerCount(client))`.
  4. `Subscribe`, then `Assert.True(observer.Query.IsResolved)`, then `Assert.Equal(1, WorkerCount)`, `Dispose`, `Assert.Equal(0, WorkerCount)`. All of these are synchronous.
- **Phase:** A.
- **Risk:** once the wait is the Task, the test is nearly the same as `Issue3_An_Awaited…`. The library has no discard-specific path (release lives in `RunAndReleaseAsync`, not in the caller's continuation), so that is honest. Keep the name, since the issue it reproduces was about the discarded path. Optionally assert between steps 1 and 2 that `WorkerCount == 1`. Per the corrected F2 the release has already happened when `QueryAsync` returns, so this assertion would be deterministic; it is optional.

### `Issue3_An_Awaited_QueryAsync_Releases_Its_Worker_Too` (`:146`)
- **Today:** deterministic, no change. It awaits the returned Task, and release precedes completion (F2).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `Issue3_Subscribe_Path_Still_Cleans_Up` (`:158`)
- **Today:** `WaitForStateAsync` (`:164`).
- **Pattern:** P1a.
- **Fix:** `Assert.True(observer.Query.IsResolved)`. The rest is unchanged and synchronous.
- **Phase:** A.
- **Risk:** none.

### `Issue4_Concurrent_QueryAsync_Keeps_The_Registry_Intact` (`:173`)
- **Today:** a barrier storm, 300 attempts × 16 `Task.Run` callers on `Barrier(16)` (`:185-191`). Then `await Task.Delay(2)` (`:193`) before checking `FindQuery(key)`. Every `QueryAsync` is awaited and release precedes completion (F2), and the background GC sweep is 1 min away (F4). Nothing remains to happen, so the delay buys nothing.
- **Pattern:** stress (probabilistic), plus a stray sleep.
- **Fix:** keep it as a **labelled probabilistic regression net**. The race it guards (concurrent `Dictionary` writes in `QueryRegistry.GetOrCreateNode`, now behind `RevalClient._gate`) happens entirely inside library code with no user callback in the window, so no gate can force it. That is the documented limitation (research topic 5, item 3). Changes:
  1. Delete `await Task.Delay(2)`.
  2. Move the class's storm tests into `[Collection("stress")]`, or move this test into a `StressTests` file under that collection.
  3. Start the 16 participants with `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` (dedicated threads). With 16 pool threads blocked in `SignalAndWait`, the barrier only releases after the pool's hill-climbing injects threads past `MinThreads`. That is a hidden clock dependency in the harness.
  4. `await Task.WhenAll(tasks).WaitAsync(HangGuard)`.
  5. Add a `// Probabilistic regression net: can find a registry race, cannot prove its absence.` comment, or `Trait("Kind","Stress")`.
- **Phase:** A (documented limitation for the race itself).
- **Risk:** `failures` also collects `InvalidOperationException("could not be run: its worker was released 3 times")` from `RunAndReleaseAsync` (`RevalClient.cs:579-581`). That outcome is reachable here for a library reason (Findings #2). If the net ever fires with that message, it is not test flakiness.

### `Issue4_Concurrent_Callers_Share_One_Fetch_Instead_Of_Stampeding` (`:203`)
- **Today:** the handler `Task.Delay(150)` (`:227`) must outlast the barrier release of all 16 callers. A caller arriving after the fetch settles sees `StaleTime 0` data as stale and fetches again, so the count becomes 2. Uses the static `SlowHandlerCalls`.
- **Pattern:** P3 + P7.
- **Fix (deterministic, keeps the concurrency):**
  1. `var handler = new GatedHandler<string>();`, with `opts = QueryOptions.Create("stampede", handler.Handle).Build()`.
  2. `var joined = new Counter(); var allJoined = new Gate();`
  3. Start 16 LongRunning tasks: `barrier.SignalAndWait(); var t = client.QueryAsync(opts); if (joined.Increment() == 16) allJoined.Open(); return t;`. Keep the *inner* `Task<string>` from each, either by using `Unwrap` or by returning the inner task through a `TaskCompletionSource`.
  4. `await allJoined.Task.WaitAsync(HangGuard)`. Every `QueryAsync` has now returned, so every caller has passed `RunAsync`'s in-flight check.
  5. `Assert.Equal(1, handler.Calls)`. No fetch can settle before release, and no release can happen before settle (`TryRelease`, `QueryWorker.cs:147-157`), so every caller found call 0 in flight.
  6. `handler.ReleaseAll("slow")`, then `var results = await Task.WhenAll(inner).WaitAsync(HangGuard)`.
  7. `Assert.All(results, r => Assert.Equal("slow", r)); Assert.Equal(1, handler.Calls);`
- **Phase:** A.
- **Risk:** this is now deterministic. Joining is decided under `_gate`, so the barrier only adds lock contention. A single-threaded variant (16 sequential `QueryAsync` calls on the test thread) proves the same contract more cheaply. Optionally add it alongside, to match TanStack's style.

### `Issue5_OnSettled_Receives_Its_Own_Params_And_Data` (`:238`)
- **Today:** the mutation handler is `Task.Delay(ctx.Params.DelayMs)` (SLOW 300, FAST 10), with `Task.Delay(50)` between the two `ExecuteAsync` calls (`:246, :258-260`). The intended order is SLOW start, FAST start, FAST settle, SLOW settle.
- **Pattern:** P3.
- **Fix:**
  1. Use a `GatedHandler<string>` for `MutationHandlerExecutionContext<Box>`. Released values are the call's own name: `Release(0,"SLOW")`, `Release(1,"FAST")`.
  2. `var slow = state.ExecuteAsync(new Box("SLOW")); await handler.Started(0).WaitAsync(HangGuard);`
  3. `var fast = state.ExecuteAsync(new Box("FAST")); await handler.Started(1).WaitAsync(HangGuard);`
  4. `handler.Release(1, "FAST"); await fast.WaitAsync(HangGuard);`. Then assert that `observed` contains exactly one entry, `("FAST","FAST")`.
  5. `handler.Release(0, "SLOW"); await slow.WaitAsync(HangGuard);`. Then assert `observed.Single(p=="SLOW").data == "SLOW"` and `observed.Single(p=="FAST").data == "FAST"`.
  6. Optionally assert `state.Data == "FAST"` (latest-wins, F6). It is deterministic, but it is new coverage, not part of the original issue.

  Add a second test, `…_When_The_First_Call_Settles_First`, with the opposite order (release 0 then 1). This is "one test per interesting ordering" (research topic 5).
- **Phase:** A.
- **Risk:** `OnMutate`/`OnSettled` run with `ConfigureAwait` defaults. Under xUnit v2's `AsyncTestSyncContext`, `observed` is mutated off the test thread, so keep the `lock`.

### `Issue6_FindQuery_Returns_Null_For_A_Mismatched_Result_Type` (`:270`)
- **Today:** `WaitForStateAsync` (`:276`).
- **Pattern:** P1a.
- **Fix:** `Assert.True(observer.Query.IsResolved)`, then the existing assertions.
- **Phase:** A.
- **Risk:** none.

### `Issue7_Colliding_Hashes_Stay_Two_Distinct_Queries` (`:283`)
- **Today:** deterministic, no change. It searches up to 4M keys, which finds a collision under any 32-bit hash with near certainty. Registration is synchronous.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** observers are never disposed. That is harmless (no polling, client disposed).

### `Issue7_Segments_Of_Different_Types_Are_Different_Queries` (`:304`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

---

## RevalQuery.Core.Tests/EvictionRaceTests.cs

Shared fixtures and statics:
- `StressCollection` definition (`:13-14`). Keep it. It is the home for every labelled stress net, including `IssueReproductionTests.Issue4_*` and `ObserverSetOptionsTests:405`.
- `EvictOnRelease` (`:87-97`). Keep it for the storm. Add `ManualEviction` (TestKit) for the new deterministic tests.
- `AssertEveryWorkerHasItsState` (`:99-116`) **walks the registry without the client's lock** while the storm mutates it (`GetOrCreateNode` adds and `PruneNode` removes `Root.Children["raced"]`). `Dictionary` enumeration can then throw `InvalidOperationException: Collection was modified`, or read a torn node. That is a **test-induced flake**. Fix: read `_gate` by reflection and `lock` it for the whole walk.

### `An_Eviction_Between_Run_Attempts_Never_Strands_A_Worker_On_A_Stateless_Node` (`:22`)
- **Today:** a storm. The handler `Task.Delay(5)` (`:30`), 8 concurrent `QueryAsync` per round, and the main loop subscribes and then silently `WaitUntilAsync(Data is not null, 1000)` (`:58`). The `Assert.Equal` at `:59` then reports a timeout as "expected from-network, got null". Unlocked registry walk (`:60`).
- **Pattern:** stress + P5 (+ P3 shape).
- **Decision: both.** The regression it guards is from commit c5562e9: `RunAndReleaseAsync` took its state once, and a retry after an eviction re-created a worker bound to the dropped state, leaving `node.Worker != null, node.State == null`. That interleaving **can** be forced with gates, by way of the supersede path in `RunAsync`. So add deterministic tests, and keep the storm as a labelled net.
- **Fix, storm (A):**
  1. Replace `:58` with `await Signals.UntilAsync(observer.Query, q => q.Data is not null)`. That subscribes first, is loud, and is bounded by `HangGuard`. A hang here is itself the bug the test names ("a state nothing is driving").
  2. Lock `_gate` in `AssertEveryWorkerHasItsState`.
  3. Replace `await Task.Delay(5)` with `await Task.Yield()` (asynchronous completion with no clock). **Then verify the net still catches the regression**: temporarily revert c5562e9's `PrepareRun`-per-attempt and run it. If `Yield` no longer hits the window, keep a non-clock widener (`Thread.SpinWait`) rather than `Task.Delay`.
  4. Label it as a probabilistic net in a comment.
- **New deterministic test 1 (A): `A_Retry_After_An_Eviction_Takes_A_Fresh_State_And_Worker`**
  1. `var eviction = new ManualEviction();` and a client with it. `var handler = new GatedHandler<string>();` with call 0 in `IgnoreCancellation` mode.
  2. `var b = client.QueryAsync(opts);` then `await handler.Started(0)`. This is synchronous per F1, so assert `handler.Calls == 1` directly. `var s1 = client.FindQuery("raced")!;`
  3. `s1.Cancel();`. This sets `_currentFetchCts.IsCancellationRequested` synchronously (`QueryWorker.cs:160-165`), but call 0 has not unwound.
  4. `var a = client.QueryAsync(opts);`. `RunAsync` sees a cancelled in-flight fetch, so it creates a superseding run whose `RunCoreAsync` awaits call 0 (`QueryWorker.cs:285-290, :313`). Assert `handler.Calls == 1`: A is parked behind the superseded fetch.
  5. `eviction.Evict(ValueTuple.Create("raced"));`. `HandleEviction` runs synchronously: S1 has no observers, so the node is cleared and pruned, and W1 is cancelled and disposed (`RevalClient.cs:640-657`). Assert `client.FindQuery("raced") is null` and `AssertEveryWorkerHasItsState`.
  6. `handler.Release(0, "stale")`. Call 0 returns. Its fetch is cancelled, so the result is dropped and B's outcome is `Cancelled`. A's run continues, and `RunFetchAsync` sees `_isDisposed` and returns `NotRun` (`QueryWorker.cs:337-340`). `RunAndReleaseAsync` then takes attempt 2.
  7. `await handler.Started(1).WaitAsync(HangGuard)`. At this point, `AssertEveryWorkerHasItsState(client)`, `Assert.NotSame(s1, client.FindQuery("raced"))`, and `Assert.NotNull(client.FindQuery("raced"))`. **This is the assertion that fails under the pre-c5562e9 code.** The node would hold a worker for S1 and no state.
  8. `handler.Release(1, "from-network")`, then `Assert.Equal("from-network", await a.WaitAsync(HangGuard))` and `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b)`.
  9. Final checks: `AssertEveryWorkerHasItsState`, `client.FindQuery<string>("raced")!.Data == "from-network"`, `handler.Calls == 2`, and `WorkerCount == 0` (A released, F2).
- **New deterministic test 2 (A): `A_Subscriber_After_An_Eviction_Mid_Fetch_Gets_A_Driven_State`**
  1. `ManualEviction`, and a `GatedHandler` that honours the token.
  2. `var a = client.QueryAsync(opts)`, then `Started(0)` (synchronous).
  3. `eviction.Evict(key)`. `HandleEviction` cancels W1, and call 0 throws OCE.
  4. `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(HangGuard))`. Then `AssertEveryWorkerHasItsState`, `FindQuery(key) is null`, and `await handler.Cancelled(0)`.
  5. `using var observer = client.Subscribe(opts, ...)`. Call 1 starts synchronously, so assert `handler.Calls == 2` and `observer.Query.IsFetching`.
  6. `handler.Release(1, "from-network")`. Then `await Signals.UntilAsync(observer.Query, q => q.IsResolved)` and assert `Data == "from-network"` plus the invariant.
- **Phase:** A (all three).
- **Risk:** in test 1, B's `ReleaseIfUnobserved(S1)` runs on the pool concurrently with A's attempt 2. It either finds no node or finds W2 in flight and sets `_releaseWhenSettled` (`TryRelease`). Both are benign, and step 9's `WorkerCount == 0` holds either way, because A's own release runs before `a` completes. With `ManualEviction`, B's and A's `RegisterForEviction` calls only record. Do not use `EvictOnRelease` here, or those records would evict S2 mid-test.

---

## RevalQuery.Core.Tests/PollingTests.cs

Shared fixtures and statics:
- `_client` (`:16-19`) is **never disposed**. `SubscribeWithRefetchInterval_Refetches` never disposes its observer, so its 50 ms poll loop keeps running on the pool for the rest of the test run and loads every other test class. Fix: make the class `IDisposable` and call `_client.Dispose()`, which disposes workers and cancels polling, and add `using` on the observers.
- `UniqueHandler` (`:109-110`, `DateTime.UtcNow.Ticks`, P7): replace with a per-test `Counter`, returning `$"data-{counter.Increment()}"`, or with a `GatedHandler`.
- All five tests use the same `Key = "poll"` on a fresh client each, which is fine.

### `SubscribeWithRefetchInterval_Refetches` (`:22`)
- **Today:** `WaitForStateAsync(IsResolved)` (`:29`, P1a). Then `WaitForStateAsync(Data != firstData, 2000)` (`:33`), which has the lost-wakeup race in `TestUtils.WaitForStateAsync` (the next poll hides it) and is driven by a real 50 ms `Task.Delay` loop.
- **Pattern:** P4 + P5 + P7.
- **Fix (A):** use a counter handler. `Subscribe`, then `Assert.Equal("data-1", observer.Query.Data)` synchronously (F1). Then `await Signals.UntilAsync(observer.Query, q => q.Data == "data-2")` with HangGuard, and `Assert.Equal("data-2", observer.Query.Data)`.
- **Fix (B):** poll timer armed synchronously in `StartPolling` (research option c). `Subscribe`, then `"data-1"`. `time.Advance(49ms)`, still `"data-1"`. `time.Advance(1ms)`, now `"data-2"`. `time.Advance(50ms)`, now `"data-3"`. Everything is synchronous, mirroring TanStack `queryObserver.test.tsx` L872-895.
- **Phase:** A, then B.
- **Risk:** in A, the real timer drives the poll, but only the signal decides the outcome. Dispose the observer.

### `DiscardedQueryAsyncThenSubscribe_InitialDataFromCache` (`:39`)
- **Today:** `Discard(QueryAsync)`, then `WaitForStateAsync` (`:46`), which is already satisfied per F1. Then `Subscribe` refetches synchronously (StaleTime 0) with `StaticHandler` "cached", which overwrites with the same value. The assertion `Equal(firstData, Data)` cannot tell "served from cache" from "refetched". It is **near-vacuous**: it fails only if `firstData` were null.
- **Pattern:** P1a + P9.
- **Fix:**
  1. Use a `GatedHandler<string>` and `ReleaseAll` only for call 0: `handler.Release(0, "cached")` before the first call, so it completes synchronously.
  2. `var fetch = _client.QueryAsync(opts); TestUtils.Discard(fetch);` then `await fetch.WaitAsync(HangGuard)`. The worker is now released (F2), so `Subscribe` really goes through the cache path.
  3. `using var observer = _client.Subscribe(opts, …)`. StaleTime 0, so call 1 starts and is held by the gate.
  4. Assert `observer.Query.Data == "cached"`, `observer.Query.IsFetching`, and `handler.Calls == 2`. The observer sees cached data while it refetches.
  5. `handler.Release(1, "fresh")`, then `await Signals.UntilAsync(observer.Query, q => q.Data == "fresh")`.
- **Phase:** A.
- **Risk:** step 3 relies on F5: the clock must tick between call 0 and `Subscribe`, or the data is "fresh" and call 1 never starts, so `Calls == 1`. If the maintainer fixes F5 (`>=`), this is exact. Otherwise under B it needs `time.Advance(TimeSpan.FromTicks(1))` before `Subscribe`. Belongs in `QueryAsync*Tests`, not `PollingTests`. Consider moving it.

### `SubscribeThenDispose_PollingStops` (`:54`)
- **Today:** asserts only `state.Data == dataBeforeDispose` straight after `Dispose()` (`:65-68`). It never checks that polling stopped. **It cannot fail** (research P9 is correct).
- **Pattern:** P9 (+ P1a at `:61`).
- **Fix (A, interim, structural):** counter handler. `Subscribe`, then `"data-1"` synchronously. `await Signals.UntilAsync(observer.Query, q => q.Data == "data-2")` is the positive control that polling was on. `observer.Dispose()`. Then `Assert.Equal(0, WorkerCount(_client))`: the worker is disposed synchronously by `OnLastSubscriberRemoved -> DisposeWorker` (`RevalClient.cs:605-609`). A disposed worker cannot fetch (`QueryWorker.cs:283`), and its polling CTS is cancelled (`:94-105, :446`). This is a proxy proof of the contract, not an observation of it.
- **Fix (B, real):** `Subscribe`, then `calls == 1`. `Advance(30ms)`, then `calls == 2`. `observer.Dispose()`. `Advance(90ms)`, then `calls == 2`. Mirrors TanStack L889-894.
- **Phase:** A (interim), then B.
- **Risk:** in A, do **not** assert "calls unchanged after Dispose". A poll that passed `CanFetch` (`QueryWorker.cs:262`) just before `Dispose` can legitimately land one more fetch, and real time cannot rule that out. Under B, `Advance` runs on the test thread, so the race is gone.

### `AQueryNobodyObservesDoesNotPoll` (`:72`)
- **Today:** `Task.Delay(200)` against a 50 ms interval (`:83`) proves an absence by waiting.
- **Pattern:** P2 (+ P1a `:79`, P7).
- **Fix (A, interim):** `var fetch = _client.QueryAsync(opts); await fetch.WaitAsync(HangGuard);` then `Assert.Equal(1, calls)` and `Assert.Equal(0, WorkerCount(_client))`. The worker was created with `CanFetch == false`, so polling was never armed (`QueryWorker.cs:86-89`), and it is now disposed (F2, F3). Document in the test that this is a structural proof, pending B.
- **Fix (B):** `await fetch`. `time.Advance(200ms)` (4 intervals). `Assert.Equal(1, calls)`.
- **Phase:** A (interim), then B.
- **Risk:** the A form tests the mechanism (no worker, no poll). If polling ever moves off the worker, it goes vacuous. B is the real fix.

### `SubscribingEnabledToAQueryOnlyDisabledObserversHold_StartsPolling` (`:90`)
- **Today:** silent `WaitUntilAsync(calls >= 3, 2000)` (`:104`). The assertion repeats the predicate. Real 40 ms polling.
- **Pattern:** P4 + P5.
- **Fix (A):**
  1. `GatedHandler<string>`.
  2. `using var disabled = Subscribe(Enabled=false)`, then `Assert.Equal(0, handler.Calls)`. This is synchronous: `CanFetch` is false and `ApplyOptions` calls `StopPolling` (`QueryWorker.cs:133-137`).
  3. `using var enabled = Subscribe(...)`, then `Assert.Equal(1, handler.Calls)` (`Subscribe -> RunIfStale`, synchronous), then `handler.Release(0, "data")`.
  4. `await handler.Started(1).WaitAsync(HangGuard); handler.Release(1,"data"); await handler.Started(2).WaitAsync(HangGuard);`. Two polls prove polling started.
- **Fix (B):** after step 3, `Advance(40ms)` then `Calls == 2`, and `Advance(40ms)` then `Calls == 3`, with a pre-released handler.
- **Phase:** A, then B.
- **Risk:** the second subscriber does not raise `OnFirstSubscriberAdded`, so polling starts only through `ApplyOptions -> StartPolling` (`:141`). That is the path under test. `wasEnabled` reads true because the new enabled observer is attached before `ApplyOptions` runs. The fetch comes from `Subscribe`'s own `RunIfStale`, not from `ApplyOptions`. Keep the `Calls == 1` assertion, since it pins that.

---

## RevalQuery.Core.Tests/EnabledToggleTests.cs

Shared fixtures and statics:
- `static int _calls` (`:13`) and the static `CountingHandler` (`:15-19`), P7: replace with a per-test `Counter`. `Probe` takes the handler as a constructor argument.
- `Probe` (`:21-35`) stays otherwise.

### `Flipping_Enabled_On_Rerender_Enables_The_Query_And_Fetches` (`:38`)
- **Today:** `Task.Delay(100)` (`:47`) before a negative assertion, and `Task.Delay(200)` (`:52`) before a positive one.
- **Pattern:** P1a + P2.
- **Fix:** delete both delays. Sequence: `first = Run(false)`, then `Assert.False(first.IsEnabled)` and `calls == 0`. `second = Run(true)`, then `Same`, `IsEnabled`, `calls == 1`, `Data == "data"`, all immediately.
- **Phase:** A.
- **Risk:** the negative at step 1 is sound only because no deferred path exists: no persistence, no `RefetchInterval` (F3, `QueryWorker.cs:200`). Say so in a comment. The positive at step 2 is synchronous through `SetOptions -> ApplyOptions -> !wasEnabled -> RunIfStale` (F1). It is also the control that proves the step 1 negative would have been visible.

### `Flipping_Enabled_Back_Off_Stops_The_Query_Fetching_Again` (`:61`)
- **Today:** `Task.Delay(200)` (`:70`), `Task.Delay(50)` (`:74`), and `Task.Delay(200)` (`:78`) before asserting `calls == 1` after `Invalidate`.
- **Pattern:** P1a + P2.
- **Fix:** delete all three. `Run(true)`, then `calls == 1`. `disabled = Run(false)`, then `Assert.False(disabled.IsEnabled)`. `Invalidate(…)`, then `calls == 1` and `Assert.True(disabled.IsInvalidated)`. The new assertion proves the invalidation reached the state and the decision not to fetch was made.
- **Phase:** A.
- **Risk:** `Invalidate` decides synchronously (`HandleInvalidation -> RunIfStaleNow -> RunIfAllowed`, where `CanFetch` is false). The research doc's P2 entry for `:74-79` is right that this needs no time.

### `A_Rerender_Applies_A_Rebuilt_StaleTime` (`:83`)
- **Today:** `Task.Delay(50/200/200/200)` (`:92, :96, :101, :107`). The research doc omits `:92`.
- **Pattern:** P1a + P2 (+ F5 at the last step).
- **Fix:** delete all four. `Run(false, 1h)`, then `calls == 0`. `Run(true, 1h)`, then `calls == 1`. `other.Run(true, 1h)`, then `calls == 1` (fresh, so no fetch). `probe.Run(true, Zero)`, then `calls == 1` (`wasEnabled`, so no `RunIfStale`; add this assertion, which is new but synchronous). `third.Run(true, Zero)`, then `calls == 2`.
- **Phase:** A.
- **Risk:** the final step needs `UtcNow - lastUpdatedAt > 0` (F5). On a fine-grained real clock that holds. Under B it needs an `Advance(1 tick)` before `third.Run`, or the library F5 fix. The intermediate `calls == 1` after `probe.Run(true, Zero)` documents that a re-render with a shorter StaleTime does not itself refetch. Confirm that is intended before adding it (TanStack: `setOptions` → `shouldFetchOptionally` *does* refetch when stale and the options changed; see Findings #4).

---

## RevalQuery.Core.Tests/StaticQueryTests.cs

Shared fixtures and statics:
- `static int _calls` (`:11`) and the static `CountingHandler` (`:13-17`), P7: use a per-test `Counter`. `Options(key, neverStale)` (`:22-29`) takes the handler as a parameter.

### `A_Static_Query_With_No_Data_Still_Fetches` (`:32`)
- **Today:** `WaitForStateAsync` (`:39`).
- **Pattern:** P1a.
- **Fix:** assert `IsResolved`, `Data == "call-1"`, and `calls == 1` right after `Subscribe`.
- **Phase:** A.
- **Risk:** none (`IsStale` returns true when there is no data, `QueryState.cs:289`).

### `A_Static_Query_Is_Not_Refetched_By_A_Later_Subscriber` (`:45`)
- **Today:** `WaitForStateAsync` (`:51`), then `Task.Delay(100)` (`:56`) before a negative.
- **Pattern:** P1a + P2.
- **Fix:** delete both. `first` is resolved, then `first.Dispose()`. `second = Subscribe`, then `calls == 1` and `Data == "call-1"` immediately.
- **Phase:** A.
- **Risk:** the negative holds because the decision is synchronous (`Subscribe -> RunIfStale -> IsStale`, `Static` returns false) and nothing deferred exists. Its paired control, `A_Non_Static_Query_Is_Refetched…`, proves the same path would fetch synchronously, so keep the two tests side by side. Under B the negative is unaffected (Static ignores time).

### `A_Non_Static_Query_Is_Refetched_By_A_Later_Subscriber` (`:62`)
- **Today:** `WaitForStateAsync` (`:68`), then a silent `WaitUntilAsync(calls == 2)` (`:73`).
- **Pattern:** P1a + P5 (+ F5).
- **Fix:** delete both. After the second `Subscribe`, `calls == 2` immediately.
- **Phase:** A.
- **Risk:** this depends on F5 (StaleTime 0 with the clock ticked). Under B, `Advance(1 tick)` is needed before the second `Subscribe` unless F5 is fixed. Without that, this control silently stops proving the negative test above. Flag it in the test.

### `Invalidating_A_Static_Query_Does_Not_Refetch_It` (`:78`)
- **Today:** `Task.Delay(100)` (`:88`) before a negative.
- **Pattern:** P2 (+ P1a `:84`).
- **Fix:** delete it. After `Invalidate`, `calls == 1` and `Data == "call-1"` immediately. Also assert `observer.Query.IsInvalidated` (synchronous, `NotifyInvalidated` sets it before raising).
- **Phase:** A.
- **Risk:** the control is `Invalidating_A_Non_Static_Query_Refetches_It` (synchronous). That control does not depend on the clock: `_isInvalidated` short-circuits before the time check (`QueryState.cs:291`).

### `An_Invalidated_Static_Query_Still_Reports_Itself_Invalidated` (`:94`)
- **Today:** `Task.Delay(100)` (`:105`).
- **Pattern:** P1a/P2.
- **Fix:** delete it, and assert `IsInvalidated` and `calls == 1` immediately.
- **Phase:** A.
- **Risk:** none.

### `Invalidating_A_Non_Static_Query_Refetches_It` (`:111`)
- **Today:** silent `WaitUntilAsync(calls == 2)` (`:121`).
- **Pattern:** P1a + P5.
- **Fix:** delete it. After `Invalidate`, `calls == 2`, `Data == "call-2"`, and `!IsInvalidated` immediately (`ApplyFetched` clears it).
- **Phase:** A.
- **Risk:** none.

### `Invalidation_Leaves_LastUpdatedAt_Alone` (`:126`)
- **Today:** `Task.Delay(100)` (`:140`).
- **Pattern:** P1a/P2.
- **Fix:** delete it and assert immediately.
- **Phase:** A. Under B, `LastUpdatedAt` becomes an exact fake instant: assert it equals `time.GetUtcNow()` at fetch.
- **Risk:** the negative is complete. Static means no refetch, so nothing else writes `_lastUpdatedAt`.

---

## RevalQuery.Core.Tests/DisposalTests.cs

Shared fixtures: `Options`/`NewClient` (`:10-14`) are fine. No statics.

### `Subscribe_After_Disposal_Throws` (`:17`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `QueryAsync_After_Disposal_Throws` (`:26`)
- **Today:** deterministic, no change. It throws inside `PrepareRun` (`RevalClient.cs:551-553`), giving a faulted Task.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `GetOrCreateQuery_After_Disposal_Throws` (`:35`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `A_Refused_Subscribe_Leaves_Nothing_Behind` (`:44`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** the test can fail only if a refused `Subscribe` registered a node, since `ThrowIfDisposedLocked` precedes `GetOrCreateNode` (`RevalClient.cs:93-95`). That is fine.

### `Teardown_After_Disposal_Is_Silent` (`:55`)
- **Today:** `Task.Delay(100)` (`:59`) after `Subscribe`. The fetch is already settled (F1).
- **Pattern:** P1a.
- **Fix:** replace the delay with `Assert.True(observer.Query.IsResolved)`, making the precondition explicit. Then the existing teardown calls. Add a sibling test, `Teardown_After_Disposal_With_A_Fetch_In_Flight_Is_Silent`:
  1. `GatedHandler` honouring the token. `Subscribe`, then `Started(0)` (synchronous).
  2. `client.Dispose()`. That cancels the current fetch (`RevalClient.cs:536`). Then `await handler.Cancelled(0).WaitAsync(HangGuard)`.
  3. `observer.Dispose(); await client.CancelAsync("f"); client.Invalidate("f"); client.Dispose();`. No exception.
  4. `Assert.True(observer.Query.IsPending)`. The cancelled result never landed.
- **Phase:** A.
- **Risk:** the original test asserts nothing beyond "no throw". That is legitimate for its contract.

---

## RevalQuery.Core.Tests/RevalClientTests.cs

Shared fixtures and statics:
- The **private `WaitUntil<T>` (`:143-157`)** is a third copy that the research doc does not list. It checks the predicate before subscribing (lost wakeup) and uses `new TaskCompletionSource()` without `RunContinuationsAsynchronously`. Delete it and use `Signals.UntilAsync` where a wait is still needed. None remains after the fixes below.
- `UniqueResultHandler` (`Guid`): use a per-test `Counter` (`res-{n}`) so the assertions can name exact values.
- `CancelableHandler` (`:171-182`, `Task.Delay(1000, ct)`): replace with a `GatedHandler`.
- `_client` is never disposed, and neither are the clients in the GC tests (`:71, :89`). Their collectors are passed in, not owned, so `RevalClient.Dispose` does not stop them. Add `using`, and `await gcCollector.DisposeAsync()`.

### `Subscribe_TriggersHandler_And_ResolvesData` (`:30`)
- **Today:** `WaitUntil(IsResolved)` (`:34`).
- **Pattern:** P1a.
- **Fix:** assert `Data == "result"` and `IsIdle` right after `Subscribe`.
- **Phase:** A.
- **Risk:** none.

### `Invalidate_Triggers_Refetch` (`:40`)
- **Today:** `WaitUntil(IsResolved)` twice (`:44, :47`). The second is already true before `Invalidate`'s refetch, so it **waits for nothing**. The test passes only because the refetch is synchronous (F1).
- **Pattern:** P1a + P5.
- **Fix:** counter handler. After `Subscribe`, `Data == "res-1"`. After `Invalidate(Key)`, `Data == "res-2"`, `calls == 2`, and `!IsInvalidated`, all immediately.
- **Phase:** A.
- **Risk:** none.

### `Hierarchical_Invalidation_Works` (`:52`)
- **Today:** keys `("users")` and `("users2")` (`:54-55`) are **siblings, not parent and child**. `Invalidate("users")` refetches only the first. The handlers return constants, and the assertions (`:61-62`) check that the data is unchanged. The test **passes if `Invalidate` does nothing at all**: it cannot fail (P9, missed by the research doc).
- **Pattern:** P9 (+ P1a).
- **Fix:**
  1. Use per-key counters.
  2. Subscribe `("users")`, `("users", 1)`, `("users", 2)` and an unrelated `("users2")`. Assert each count is 1 immediately.
  3. `_client.Invalidate(ValueTuple.Create("users"))`.
  4. Assert immediately: `("users")` == 2, `("users",1)` == 2, `("users",2)` == 2, `("users2")` == 1. Then `_client.Invalidate(("users", 1))` and assert only `("users",1)` becomes 3.
- **Phase:** A.
- **Risk:** prefix semantics are `PeekNode(prefix)` followed by `StatesFrom(node)`, which includes the node itself (`RevalClient.cs:659-666`, `QueryRegistry.cs:227-232`). Confirm `("users")` and `("users",1)` share the first segment node, which they do per `GetOrCreateNode`.

### `GarbageCollection_Removes_State_After_TTL` (`:66`)
- **Today:** `WaitUntil` (`:76`, P1a). A manual `CollectExpiredEntries` with `GcTime = 0` against `DateTime.UtcNow` (residual P4). The client and collector are never disposed.
- **Pattern:** P1a (+ P4 residual).
- **Fix:** delete the wait. `Assert.True(IsResolved)`. `Dispose`. `Collect`. `Assert.Null`. Add `using`/`DisposeAsync`.
- **Phase:** A (B makes the expiry comparison exact).
- **Risk:** none.

### `Resubscribe_Cancels_Eviction` (`:84`)
- **Today:** `GcTime = 10 s` (`:87`), so `CollectExpiredEntries` would not evict even if `CancelEviction` never ran. `HandleEviction` also refuses an observed query (`RevalClient.cs:647`). **The test is doubly vacuous**: it cannot fail through the behaviour it names.
- **Pattern:** P9 (+ P1a `:94`).
- **Fix:**
  1. Set `GcTime = TimeSpan.Zero`.
  2. Record evictions: `var evicted = new List<ITuple>(); gcCollector.OnEvictionRequired += evicted.Add;`.
  3. `Subscribe`, resolved (synchronous). `Dispose`. `Subscribe` again (synchronous `CancelEviction`, `RevalClient.cs:604`). `gcCollector.CollectExpiredEntries()`.
  4. `Assert.Empty(evicted)` (the death-row entry was removed, so the policy never asked for eviction), and `Assert.NotNull(client.FindQuery(Key))`.
- **Phase:** A.
- **Risk:** without step 4's `Empty(evicted)`, the `HasObservers` guard alone would keep the test green. The event assertion is what makes it test `CancelEviction`.

### `RevalClient_Cancel_AbortsFetch` (`:103`)
- **Today:** `CancelableHandler` = `Task.Delay(1000, ct)`. `WaitUntil(IsFetching)` (`:108`) is already true synchronously. After `await CancelAsync`, `WaitUntil(IsIdle)` (`:110`) is also already true. **If cancellation did not work**, `CancelAsync` would wait for the in-flight fetch to finish after 1 s, `IsIdle` would be true, and the test would **pass**. It cannot fail through the behaviour it names. It is just slower.
- **Pattern:** P9 + P1a + P5.
- **Fix:**
  1. `GatedHandler<string>` honouring the token.
  2. `Subscribe`, then `Assert.Equal(1, handler.Calls)` and `Assert.True(observer.Query.IsFetching)`, synchronously.
  3. `await _client.CancelAsync(Key).WaitAsync(HangGuard)`. If cancellation is broken, this hangs on the never-released gate and HangGuard fails it loudly.
  4. `Assert.True(handler.Cancelled(0).IsCompleted)`, then `IsIdle`, `IsPending`, `Data is null`, and `Exception is null` (a cancelled query records no error, `RevalClient.cs:154`).
- **Phase:** A.
- **Risk:** the default query retry is 3 (`RetryOptions.cs:42`). The retry policy does not retry once the token is cancelled (`ExponentialBackoffRetryPolicy.cs:312`), so `Calls` stays 1. Assert it.

### `Plugin_Throws_On_NonStatic_Handler` (`:115`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `Plugin_Allows_Static_Handler` (`:124`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

### `QueryOptions_Create_With_String_Key_Succeeds` (`:133`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** none.
- **Risk:** none.

---

## Counts

There are 45 existing tests.

| Group | Count | Tests |
|---|---|---|
| ok, no change | 12 | IssueRepro 5, Disposal 4, RevalClient 3 |
| Phase A only | 28 | Two of these are documented-limitation stress nets: `Issue4_Concurrent_QueryAsync` and the `EvictionRace` storm |
| Phase A interim, then B for the real fix | 5 | `Issue2_BackgroundEviction`, plus all of `PollingTests` except `Discarded…` |
| NEEDS-SIGNAL | 0 | Only the new MaxRunAttempts repro in Findings #2 needs one. No existing test does. |

New tests proposed (all Phase A):
- `EvictionRaceTests`: 2.
- `Issue5`: 1, for the second ordering.
- `DisposalTests`: 1, in-flight teardown.
- `Issue4` stampede: optionally a single-threaded variant.

## Findings

1. **Suspected library bug: StaleTime zero is not "always stale".** `QueryState.IsStale` uses a strict `>` (`QueryState.cs:293`). `CoreFetchOptions.Default` documents StaleTime zero as "always stale" (`FetchOptions.cs:12-13`). TanStack's `timeUntilStale` returns 0 when `updatedAt + staleTime == now`, which makes the query stale (`utils.ts` L158, cited in the research doc). In RevalQuery, a subscriber arriving within the same clock tick as the last fetch is served "fresh" data and does not refetch.
   - On a coarse clock this is a real behaviour difference. I have not verified the Blazor WASM clock resolution, and I would not call it settled until someone measures it.
   - Under the planned `FakeTimeProvider` (phase B), it **always** happens without an `Advance`. That silently breaks every control test that relies on refetch-on-subscribe with StaleTime zero: `StaticQueryTests.A_Non_Static…`, `EnabledToggleTests.A_Rerender…` final step, and `PollingTests.Discarded…`.
   - The research doc's phase B section misses this. It claims phase B makes freshness "exact", but exact equality is exactly the broken case.
   - Fix candidate for the maintainer: `>=`, which matches TanStack.
2. **Suspected library defect: `QueryAsync` can fail under contention for reasons unrelated to the query.** `RunAndReleaseAsync` retries `NotRun` at most `MaxRunAttempts = 3` (`RevalClient.cs:28, 561-581`), then throws `InvalidOperationException("could not be run…")`. With N concurrent unobserved callers and a fast handler, each settle releases the worker (`ReleaseIfUnobserved`). A caller between `PrepareRun` and `worker.RunAsync` can be handed a disposed worker repeatedly.
   - Attempt 1 is reachable deterministically, as `EvictionRaceTests` new test 1 shows. Three in a row is reachable in the storms (`Issue4_Concurrent_QueryAsync`, the EvictionRace storm), so if those nets fire with this message, the cause is in the library and not in the tests.
   - TanStack's `fetchQuery` has no such bound.
   - **NEEDS-SIGNAL** for a deterministic repro of exhaustion: the test must know when a retrying caller has joined the next worker. A superseded run parks silently, and no public signal exists. That needs an internal seam between `PrepareRun` and `RunAsync`, or an observable "run attempt started" hook. It is the maintainer's call, and it is not needed for any existing test.
3. **Test-induced flake.** `EvictionRaceTests.AssertEveryWorkerHasItsState` (`:99-116`) enumerates `RegistryNode.Children` without `RevalClient._gate` while the storm mutates it. It can throw "Collection was modified" or read torn state. Fix: lock `_gate`.
4. **Behaviour to confirm (not a bug claim).** A re-render that shortens StaleTime on an already-enabled query does not refetch: `ApplyOptions` calls `RunIfStale` only when `!wasEnabled` (`QueryWorker.cs:143`). In TanStack, `QueryObserver.setOptions` calls `shouldFetchOptionally` when options change, which refetches stale data. `EnabledToggleTests.A_Rerender…` currently does not pin either behaviour at that step. I verified the RevalQuery side. The TanStack side is from my reading of `queryObserver.ts` and should be checked before deciding.
5. **Tests that cannot fail today:**
   - `PollingTests.SubscribeThenDispose_PollingStops` (the research doc has this one).
   - `PollingTests.DiscardedQueryAsyncThenSubscribe_InitialDataFromCache`, which is near-vacuous: a constant handler plus a synchronous refetch.
   - `RevalClientTests.Hierarchical_Invalidation_Works`: sibling keys and constant handlers, so it passes if `Invalidate` is a no-op.
   - `RevalClientTests.Resubscribe_Cancels_Eviction`: a 10 s `GcTime`, plus the `HasObservers` guard.
   - `RevalClientTests.RevalClient_Cancel_AbortsFetch`: passes without cancellation, just 1 s slower.
6. **Leaks that load the whole run.** In `PollingTests`, neither the client nor the observer from `SubscribeWithRefetchInterval_Refetches` is disposed, so a 50 ms poll loop runs for the rest of the process. The `RevalClientTests` GC tests leave their collectors' loops running. This feeds the thread-pool contention the research doc blames for P1/P3 flakes.
7. **Research doc corrections, for these files:**
   - (a) P5 lists two private `WaitUntil` copies. A third, event-driven one with the same lost-wakeup race is at `RevalClientTests.cs:143-157`.
   - (b) The P1 and P2 lists for `EnabledToggleTests` omit `:92`.
   - (c) The P1a "verified behaviour" also holds for `QueryAsync`: its Task is already complete, worker released, when it returns (corrected F2).
   - (d) The `Issue4` stampede fix should also note the barrier-on-pool-threads time dependency.
   - (e) Option (c) and "StaleTime exact" in the phase B section need Finding #1.
   - (f) P9 misses the three vacuous `RevalClientTests` and the near-vacuous `Discarded…` test.
   - The line references I checked (`EnabledToggleTests`, `StaticQueryTests`, `DisposalTests:59`, `PollingTests:25/57/75/83/98/104/110`, `IssueReproductionTests:98-110/222/226-228/246-260/348-352`, `EvictionRaceTests:13-17/58`) are otherwise correct.
