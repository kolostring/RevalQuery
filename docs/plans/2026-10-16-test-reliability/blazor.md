# G5: RevalQuery.Blazor.Tests reliability plan

Scope: `RevalQuery.Blazor.Tests/` (30 tests: RevalRendererTests 19, PrerenderTransferTests 8, ReviewOf1784595Tests 3), plus `Fx.cs`, `PersistentStateHarness.cs` and the `.razor` fixtures.

How I checked: I read the library code. I also ran probes in a **scratch copy** of the repo on net8.0 and net10.0. The net9 runtime is not installed locally; its metadata for the types involved is identical to net8. The repo itself was not touched. Probe results are quoted where they settle a question.

## Ground truth that the fixes rely on

1. **No persistence + completed handler = synchronous.** `Fx.Detail`'s handler (`Fx.cs:40-45`) is an `async` lambda that awaits only when a gate is registered for that id, so ungated it completes synchronously. A read during render therefore subscribes and resolves inside that render (`QueryWorker.cs:275-392`, retry first attempt awaits nothing, `ExponentialBackoffRetryPolicy.cs` loop at attempt 0).
2. **Renders from library changes.** `RevalRenderer.cs:54-55`: every observer change posts `Component.HandleEventAsync` to the renderer dispatcher. `ComponentBase.HandleEventAsync` then calls `StateHasChanged`. So any `OnChanged` on a query or mutation read through the hooks produces a render of the owning component. That includes a component whose markup hides the value (see the P6 correction below). Before `Attach`, a change sets `_pending`, and `Attach` flushes one render (`RevalHooks.Attach`). This is the "one extra render" in `A_Query_That_Completes_During_The_Read...`.
3. **Persistence = genuinely async, with a deterministic end signal.** With `AddRevalQueryPrerenderTransfer`, `RevalClient` has a non-null persistence. `GetOrCreateQueryLocked` calls `BeginRestore()` and then `Task.Run(LoadPersistedAsync)` (`RevalClient.cs:117-121`). `Subscribe`'s `RunIfStale` hands its decision to the restore (`QueryWorker.cs:248-253`, `QueryState.TryDeferUntilRestored`). `CompleteRestore` runs that decision **before** it completes `RestoreCompleted` (`QueryState.cs:418-447`). With a `Task.FromResult` handler, the decided fetch also finishes inline. So **once `QueryState.RestoreCompleted` has completed, the restore and any fetch it caused are already applied**. If the load beat `RunIfStale` (no deferral), the fetch ran synchronously inside `Subscribe` instead, and the conclusion is the same. Probe: 300 iterations each of "transfer reused" and "invalidated, refetched" on net8 and net10, asserting immediately after `await RestoreCompleted`, gave 0 failures.
4. **`QueryAsync` awaits the save.** `RunCoreAsync` completes the run only after `RunFetchAsync`, which awaits `SavePersistedAsync` (`QueryWorker.cs:389, 315-323`). A synchronous store's `SaveAsync` therefore has run by the time `await client.QueryAsync(...)` returns.
5. **Default query retry is 3 retries with real `Task.Delay` of 2 s, 4 s and 8 s** (`RetryOptions.cs:35-42`, `ExponentialBackoffRetryPolicy.cs:37`). Probe: a failing server `QueryAsync` takes **14 010 ms** on both TFMs.
6. **`MutationState<,>` does not implement `IMutationState`.** No type implements it, and its members are named differently (`IsError`/`IsSuccess`/`Error` against `IsException`/`IsResolved`/`Exception`). So the shared `Signals.UntilAsync(IMutationState, …)` cannot accept the only mutation state that exists. TestKit needs `Signals.UntilAsync<TP, TR>(MutationState<TP, TR>, Func<MutationState<TP, TR>, bool>)`, an overload on the public concrete type that uses its `OnChanged` (`MutationState.cs:84`). This is not NEEDS-SIGNAL, because the event exists.
7. **bUnit 1.40.** `TestContextBase.DefaultWaitTimeout` is a settable static and defaults to 1 s (probe printed `00:00:01`). The `WaitFor*` helpers re-check on every render of the cut or its children.

### File-independent decision for every bUnit `WaitForAssertion`/`WaitForState`

They are render-driven and sound wherever the awaited state ends in a render of the cut. Their one non-deterministic element is the 1 s ceiling, which **does** decide pass/fail on a slow agent. Fix (phase A, once per project): add a `[ModuleInitializer]` in Blazor.Tests that sets `TestContextBase.DefaultWaitTimeout = HangGuard`, so the bUnit ceiling becomes the same hang guard as everywhere else. Don't pass per-call timeouts. Each entry below refers to this as **"bUnit-HG"**.

---

## RevalQuery.Blazor.Tests/RevalRendererTests.cs

File-level: no statics. `Fx` is per test instance, because xUnit makes a new class instance (and a new `TestContext`) per test.

Shared fixtures to change:
- `private static TaskCompletionSource Gate()` (`:12`) → TestKit `Gate` (`gate.Open()` replaces `SetResult()`).
- `TestUtils.UntilAsync` (`:383-394`) → delete. Its two Blazor users move to `Signals.UntilAsync` or `RestoreCompleted`. It hot-spins `Task.Yield()` (P5).
- `Flush` (`:23`) stays. It is a deterministic dispatcher drain, not a sleep: one `InvokeAsync` queues behind every work item already posted.

### `A_Query_Goes_From_Loading_To_Data_Without_The_Page_Asking_To_Render` (`RevalRendererTests.cs:30`)
- **Today:** `"loading"` is asserted synchronously after render (`:37`). The handler is parked on `fx.Gate(1)`. Then `gate.SetResult()` and `WaitForAssertion(item1)` (`:40`).
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG. `fx.Gate(1)` becomes a TestKit `Gate` and is released with `Open()`. Keep `WaitForAssertion`: the outcome is a render triggered by `OnChanged` → `RevalRenderer` → `HandleEventAsync`, which is exactly what the test claims. Keep `CallsFor(1) == 1`.
- **Phase:** A.
- **Risk:** Don't replace this with `Signals.UntilAsync` on the query state. The point is that the page re-renders without being asked, and only a render-driven wait proves that.

### `A_Query_That_Completes_During_The_Read_Costs_One_Extra_Render_That_Settles` (`:45`)
- **Today:** deterministic. Everything is synchronous and dispatcher-queued. `Flush` drains the `_pending` render queued by `Attach` (`:50`). Then two more flushes prove that nothing else renders (`:56-59`).
- **Pattern:** ok, but the assertion is weak (P9).
- **Fix:** Replace `Assert.InRange(renders, 1, 2)` (`:54`) with `Assert.Equal(2, renders)`. The test name says *one extra* render, and the probe printed exactly 2 after one flush on net8 and net10. The absence check at `:59` is sound without any timing: no async source is left (no gate, no persistence, no poll, RecordingEviction has no timer), so a drained dispatcher is final.
- **Phase:** A.
- **Risk:** Today a regression to 1 render (lost `_pending` flush) would pass. Note that a lost flush would also leave the page showing stale state.

### `A_Read_In_A_Later_Batch_That_Changes_State_Re_Renders_The_Page` (`:63`)
- **Today:** `WaitForAssertion(#own == item1)` (`:69`), then flush and render-count stability (`:70-76`).
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG and nothing else. Keep `WaitForAssertion` rather than counting flushes: the chain is Popover `OnAfterRender` → posted show → child read → `OnChanged` → posted LateReader render. That is two dispatcher hops queued *behind* any single flush, so "N flushes" would hard-code the hop count.
- **Phase:** A.
- **Risk:** none.

### `Switching_Key_Releases_The_Old_Query_And_Keeps_The_New_One` (`:80`)
- **Today:** deterministic, no change. The new key resolves inside the render (ground truth 1). The sweep in `OnAfterRender` releases key 1 synchronously, and `RecordingEviction` records it synchronously.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A (no-op).
- **Risk:** A posted re-render (from the new key's notifications) may still run after the assertions. It reads only key 2 and changes nothing asserted.

### `A_Loop_Releases_What_It_Dropped_And_Keeps_The_Rest_Unfetched_Again` (`:95`)
- **Today:** deterministic, no change (same reasoning as above).
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `A_Helper_Called_Twice_And_Two_Reads_On_One_Line_Each_Keep_Their_Query` (`:111`)
- **Today:** deterministic, no change. Synchronous reads and sweeps.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `Content_Rendered_In_A_Later_Batch_Is_Not_Released_And_Nothing_Refetches` (`:126`)
- **Today:** `WaitForAssertion(3 items with data)` (`:134`) waits for the Popover's posted show. Then 4 ticks, `Flush`, and the asserts `Observed` and `TotalCalls == 4`.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG. The "nothing refetches" absence at `:141` is sound without waiting. With `Stale = 0`, any re-subscribe would refetch **synchronously inside the render** (ground truth 1), so a refetch would already be counted when `SetParametersAndRender` returns. The `Flush` covers posted re-renders.
- **Phase:** A.
- **Risk:** Keep `Fx.Stale = 0` (the default). It is what makes the absence provable. With a non-zero stale time, a refetch bug would be masked.

### `Later_Batch_Content_Still_Releases_The_Keys_It_Stops_Reading` (`:144`)
- **Today:** `WaitForAssertion` (`:149`), then synchronous param changes, `Flush`, asserts.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG.
- **Phase:** A.
- **Risk:** none.

### `Content_A_Child_Hides_While_It_Loads_Is_Not_Released_And_Not_Refetched` (`:161`)
- **Today:** The first render is fully synchronous (`Gated` with `Task.CompletedTask`), so the asserts at `:167-168` hold without waiting. Three renders with a pending gate (`:171-172`) and synchronous asserts follow. Then `gate.SetResult()` and `WaitForAssertion(3 with data)` (`:178`), `Flush`, asserts.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG. Use TestKit `Gate` (`gate.Open()`). Opening it posts all three `Gated.OnParametersSetAsync` continuations to the dispatcher at once (RunContinuationsAsynchronously plus the captured renderer context). The later `Flush` queues behind all three, so the absence assertions at `:181-183` are final.
- **Phase:** A.
- **Risk:** none. `RegisteredCount == 0` proves nothing was swept while hidden. Keep it.

### `A_Hidden_Branch_Keeps_Its_Query_Until_It_Renders_Again` (`:186`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `A_Running_Mutation_Survives_Renders_And_Its_State_Reaches_The_Page` (`:204`)
- **Today:** `WaitForAssertion("Fetching")` (`:214`). `Status = Fetching` and `NotifyChanged` happen synchronously at the top of `ExecuteAsync` (`MutationState.cs:131-143`), and `ComponentBase` re-renders before the event task completes, so this already holds on the first check. Then hide, ticks, `gate.SetResult()`, `TestUtils.UntilAsync(() => state.IsResolved)` (`:220`, hot spin, 10 s, P5).
- **Pattern:** P6 (`:214`, sound) and P5 (`:220`).
- **Fix:** bUnit-HG for `:214`. Replace `:220` with `await Signals.UntilAsync(state, s => s.IsResolved)`, using the `MutationState<,>` overload (ground truth 6). The order becomes: `gate.Open()` → `await Signals.UntilAsync(state, IsResolved)` → `SetParametersAndRender(Show = true)` → assert `Same(state, Last)`, `"Resolved"`, `"saved"`.
- **Phase:** A.
- **Risk:** `Status`/`Data` are written in one lock *before* the trailing `NotifyChanged` (`MutationState.cs:161-170` vs `:247`). The subscribe-then-check in `Signals` can therefore complete before that notification. That is harmless, because both fields are already set and the late notification only causes an extra render. The research doc's reason for using `UntilAsync` here is wrong (see Findings): `cut.WaitForState(() => state.IsResolved)` would also work. I still prefer `Signals` because the predicate is library state, not markup.

### `A_Mutation_Runs_With_The_Options_Of_The_Latest_Render` (`:228`)
- **Today:** `WaitForAssertion(.data == "second")` (`:237`). The handler awaits `Task.CompletedTask`, so this already holds on the first check.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG.
- **Phase:** A.
- **Risk:** none.

### `Disposing_The_Page_Releases_The_Hooks_And_Every_Query` (`:240`)
- **Today:** deterministic, no change. `DisposeComponents` is synchronous, and so is `RevalRenderer.Dispose` → `HostLink.Dispose` → `Release` → observer disposal → `RegisterForEviction`.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `Swapping_The_Hooks_Releases_The_Old_Ones_And_Their_Queries` (`:255`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `A_Renderer_In_Always_Rendered_Child_Content_Still_Re_Renders_The_Owning_Component` (`:269`)
- **Today:** like the first test. Gate, then `WaitForAssertion` (`:279`), then `Flush`.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG and TestKit `Gate`.
- **Phase:** A.
- **Risk:** none.

### `A_Second_Renderer_On_The_Same_Hooks_Throws_On_Every_Parameter_Set` (`:285`)
- **Today:** deterministic, no change. `second.SetParametersAsync` throws synchronously in `OnParametersSet` → `Attach`.
- **Pattern:** ok.
- **Fix:** none (optionally narrow `ThrowsAny<Exception>` at `:300` to the bUnit-wrapped `InvalidOperationException`).
- **Phase:** A.
- **Risk:** none.

### `A_Renderer_Without_A_Component_Says_So` (`:309`) / `A_Renderer_Without_Hooks_Says_So` (`:320`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `An_Owner_Without_ComponentBase_Is_Re_Rendered_Through_Its_Own_Handler` (`:331`)
- **Today:** `Hooks.Query(...)` is called off-dispatcher from the test thread, so the fetch parks on the gate and its `Fetching` notification produces exactly one `HandleEventAsync`. Then `WaitForState(Events > 0)` (`:339`), a snapshot, `gate.SetResult()`, and `WaitForState(Events > events)` (`:344`). `HandOwner` renders on every `HandleEventAsync`, so both waits end in a render.
- **Pattern:** P6 (sound).
- **Fix:** bUnit-HG and TestKit `Gate`. After the second wait, add `Assert.True(fx.State(1)!.IsResolved)` and `Assert.Equal("item1", fx.State(1)!.Data)`. That ties the second event to the resolution and not to some other notification.
- **Phase:** A.
- **Risk:** The second half is only meaningful because exactly one notification happens before the gate. If the library ever added a second pre-gate `NotifyChanged`, `Events > events` could pass with the gate never opened. The added asserts close that gap.

---

## RevalQuery.Blazor.Tests/PrerenderTransferTests.cs

File-level statics: `static int _serverCalls, _clientCalls` (`:24-25`), incremented from `static` lambdas (`:28-40`). That is P7. Fix: give `ServerQuery`/`ClientQuery` a `Counter` parameter (non-static lambdas), create one `server` and one `client` `Counter` per test, and delete the resets at the top of each test.

Shared fixtures to change:
- `Host(...)` (`:47-61`) returns the root provider, but the scope from `CreateScope()` is never disposed. `provider.Dispose()` does not dispose child scopes, so every test leaks a `RevalClient`, a `PrerenderTransfer` subscription and a `TtlQueryGarbageCollector` whose collection loop (`TtlQueryGarbageCollector.cs:141-151`) was started by `QueryAsync`'s `RegisterForEviction` and is never stopped. Return the `IServiceScope` and dispose **it** (then the provider). The same applies to `ReviewOf1784595Tests` tests 1-2 and `The_Transfer_Joins...` (`:279-288`).
- `Restored<TKey>` helper (`:241-255`): `Task.Delay(200)` → `RestoreCompleted` (see `Keys_That_Differ…`).
- `RecordingPersistence` (`:301-315`): keep it. It is synchronous and records saves, so it is already deterministic. Add a `Loads` counter for the strengthened `Joins` test. `GatedPersistence` is not needed: the persistence under test is the real `PrerenderTransfer`, and the async boundary is `RevalClient`'s `Task.Run`, whose end signal is `RestoreCompleted`.
- `FindTimestamp` (`:293-299`) is broken (see `Stale_Transferred…`). Replace it with a decode, rewrite and re-encode helper.
- `PersistentStateHarness`: see its own section.

Common client-side pattern ("**await-restore**"): `var observer = client.Subscribe(ClientQuery(key, counter, …), () => {}); var state = client.FindQuery<Widget>(key)!; await state.RestoreCompleted.WaitAsync(HangGuard);`. After that point, everything the restore led to has already happened (ground truth 3). `RestoreCompleted` is on `QueryState`, not on `IQueryState`, so get it via `FindQuery<Widget>(key)`. Don't cast `observer.Query`.

### `A_Prerendered_Query_Reaches_The_Client_Without_A_Second_Fetch` (`PrerenderTransferTests.cs:63`)
- **Today:** server side deterministic (awaited). Client: `Subscribe`, then `Task.Delay(300)` (`:85`), then assert data == server and `_clientCalls == 0`.
- **Pattern:** P1 (data present) plus P2 (no client fetch) plus P7.
- **Fix:** await-restore. Then assert `state.Data == Widget(7, "from server")`, `client.Count == 0`, `state.IsFetching == false`. The absence is proven, not sampled: with `StaleTime(5 min)`, the only decision point (the deferred `RunIfStale`) has already run, and nothing else (no poll, no invalidation) can start a fetch.
- **Phase:** A.
- **Risk:** This is a near duplicate of `A_Transfer_Without_An_Invalidation_Is_Reused` (`:124`). Keep this one (it also checks the server count and `written` is non-empty) and either delete `:124` or give it a distinct claim.

### `An_Invalidation_From_The_Prerender_Crosses_To_The_Client` (`:94`)
- **Today:** `TestUtils.UntilAsync(_clientCalls > 0)` (`:115`), then `Equal(1)` and the data.
- **Pattern:** P5 plus P7.
- **Fix:** await-restore. Then assert `client.Count == 1` and `state.Data == Widget(9, "from client")`. The restored `IsInvalidated` makes `IsStale` true, and the deferred decision fetched inline before completion.
- **Phase:** A.
- **Risk:** This is stronger than today. "Exactly 1 at the moment the restore ends" also rules out a double fetch that today's `> 0` wait could race past.

### `A_Transfer_Without_An_Invalidation_Is_Reused` (`:124`)
- **Today:** `Task.Delay(300)` (`:141`), then the asserts.
- **Pattern:** P1 plus P2 plus P7.
- **Fix:** same as `A_Prerendered_Query…`: await-restore, then `Count == 0`, data == server. Or merge the two tests (see the Risk under the first).
- **Phase:** A.
- **Risk:** duplicate test.

### `Stale_Transferred_Data_Is_Shown_And_Then_Refetched` (`:150`)
- **Today:** **vacuous.** The payload stored under the key is the JSON encoding of a string, so the inner quotes appear as `"`. The marker `"LastUpdatedAt":"` is never found (probe: `markerIndex: -1` on net8 and net10). `FindTimestamp` therefore returns a garbage substring that starts at index 16, and `Replace` corrupts the payload. `TryTakeFromJson` then throws, `LoadPersistedAsync` swallows it, nothing is restored, and the client simply fetches. So `_clientCalls == 1` and client data pass **whether or not stale transferred data is refetched, and whether or not it is shown first**. It also waits with `UntilAsync` (`:172`). Separately, "is shown" is never asserted.
- **Pattern:** P9 (cannot fail) plus P5 plus P7.
- **Fix:**
  1. Rewrite the timestamp properly. Use `var inner = JsonSerializer.Deserialize<string>(written[key])!`, replace the value of `"LastUpdatedAt":"…"` inside `inner` (a regex on the decoded string, and `Assert.True(match.Success)` so this can never silently do nothing again), then `written[key] = JsonSerializer.SerializeToUtf8Bytes(inner)`.
  2. Make the client handler a `GatedHandler<Widget>` (StaleTime 5 min).
  3. await-restore. Assert `state.Data == Widget(7, "from server")` (**shown**), `state.IsFetching == true`, `handler.Calls == 1` (**refetch started**). The fetch has already started because `CompleteRestore` runs the decision before completing.
  4. `handler.Release(0, Widget(9, "from client"))`, then `await Signals.UntilAsync(state, s => s.IsIdle && s.Data == Widget(9, …))`. Assert `handler.Calls == 1`.

  Probe: this exact sequence passed 200 out of 200 on net8 and net10.
- **Phase:** A.
- **Risk:** The 2020 timestamp is compared with the real clock (`QueryState.cs:293`), with a margin of years. That is fine today. If phase B later gives these hosts a `FakeTimeProvider` (default start 2000-01-01), a 2020 timestamp would be **in the future** and the query would read fresh. The rewrite must then use `fake.GetUtcNow() - (StaleTime + 1 tick)`.

### `A_Failed_Query_Is_Not_Transferred` (`:181`)
- **Today:** The server `QueryAsync` with a failing handler uses the default retry (3 retries, real 2+4+8 s backoff), so the test takes **14 s** of wall clock (probe: 14 010 ms). That is P4. The client then waits with `UntilAsync(Data != null, timeoutMs: 15_000)` (`:203`, P5). The 15 s ceiling is cargo: the client handler succeeds first time. `Assert.Empty(written)` (`:196`) is deterministic.
- **Pattern:** P4 plus P5 plus P7.
- **Fix:** Give the failing server query `.ConfigureRetry(r => r.Retry(0))`, so there is one attempt and no `Task.Delay`. Keep `ThrowsAnyAsync` but narrow it to `InvalidOperationException` "boom". Client: await-restore, then `client.Count == 1` and data == client widget.
- **Phase:** A.
- **Risk:** `Assert.Empty(written)` would also pass if the harness silently ran zero callbacks (see `PersistentStateHarness`). Add a positive control: in the same server host, also resolve a second, covered key, then assert that `written` holds the payload and that it contains that key but not `"widget"`. Today an empty `written` is ambiguous between "failure not transferred" and "persist never ran".

### `Keys_That_Differ_Only_By_Segment_Type_Or_Punctuation_Do_Not_Collide` (`:212`)
- **Today:** server side deterministic. The `Restored` helper sleeps `Task.Delay(200)` (`:250`) before reading `Data`. If the restore is slower, `Data` is null (the fetch is deferred while restoring), and the test flakes.
- **Pattern:** P1.
- **Fix:** In `Restored`: `Subscribe`, then `await client.FindQuery<Widget, TKey>(key)!.RestoreCompleted.WaitAsync(HangGuard)`, then read `Data?.Name`. Also assert the handler (which returns `"refetched"`) was not called, via a `Counter`, so a collision that drops an entry fails as "fetched" and not as a vague mismatch.
- **Phase:** A.
- **Risk:** none. Note that the four keys share one client, and each `Restored` disposes its observer before the next one.

### `Registration_Without_A_TypeInfoResolver_Fails_Loud` (`:257`)
- **Today:** deterministic, no change.
- **Pattern:** ok.
- **Fix:** none.
- **Phase:** A.
- **Risk:** none.

### `The_Transfer_Joins_A_Persistence_Store_Rather_Than_Replacing_It` (`:268`)
- **Today:** `await client.QueryAsync(...)` then `Task.Delay(200)` (`:284`) then `Saves == 1`. The sleep is unnecessary: `QueryAsync` awaits the save (ground truth 4).
- **Pattern:** P1a plus P9 (weak).
- **Fix:**
  1. Delete the delay and assert `store.Saves == 1` straight after `await QueryAsync`.
  2. The test registers the store **after** the transfer. The failure it is named for (the transfer registration replacing or `TryAdd`-skipping a user store) shows up mainly in the other order, which is never exercised. Make it a `[Theory]` over both registration orders. In each order: (a) `Saves == 1` after `QueryAsync`; (b) the transfer still participates on load. Build the client state from a server payload for `"widget"`, subscribe with StaleTime 5 min, await-restore, and assert data == server widget and client count 0.
  3. Dispose the scope.
- **Phase:** A.
- **Risk:** The README says registration order sets precedence ("Call this first to have transferred data preferred"). If that is a contract, add `store.Loads == 0` in the transfer-first order, where the composite returns on the first hit.

---

## RevalQuery.Blazor.Tests/ReviewOf1784595Tests.cs

File-level: no statics. The scopes from `CreateScope()` are never disposed (`:20`, `:42`), with the same leak as `Host` above. Dispose them.

### `Persisting_Skips_A_Type_The_Resolver_Does_Not_Cover` (`ReviewOf1784595Tests.cs:24`)
- **Today:** deterministic, no change needed for timing. Both `QueryAsync` calls are awaited, and `PersistAsync` snapshots synchronously.
- **Pattern:** ok.
- **Fix:** Dispose the scope. Optional: assert on the encoded key `"System.String:covered"` and not on the substring `"covered"`, which `"uncovered"` also contains. The pair of asserts is sound together, but each one alone is misleading.
- **Phase:** A.
- **Risk:** Background `LoadPersistedAsync` tasks (one per key, `Task.Run`) can still be running when the test ends. They are harmless, because `TryRestore` is refused after the query settled, and disposing the scope is enough.

### `Loading_Skips_A_Type_The_Resolver_Does_Not_Cover` (`:54`)
- **Today:** deterministic, no change. `transfer.LoadAsync` is awaited directly, with no `RevalClient` on the client side.
- **Pattern:** ok.
- **Fix:** Dispose the server scope. Add a positive control: with the *Gizmo* resolver on the client, the same payload loads `Gizmo(2)`. Without it, `Null` would also pass if the payload never arrived (for example, the harness persisted nothing).
- **Phase:** A.
- **Risk:** Same harness ambiguity as `A_Failed_Query_Is_Not_Transferred`.

### `Disposing_The_Prerender_Transfer_Twice_Is_Harmless` (`:82`)
- **Today:** deterministic, but **it cannot fail for the regression it guards**. Removing `PrerenderTransfer`'s `_isDisposed` guard (`PrerenderTransfer.cs:117-121`) changes nothing observable, because the framework's `PersistingComponentStateSubscription.Dispose` is itself idempotent. Probe: a double `Dispose()` on net8 and net10 does not throw, and the callback is not invoked afterwards. The test fails only if `Dispose` throws.
- **Pattern:** P9.
- **Fix:** Keep the `Assert.Same` (it proves the single instance is under two service types, so DI disposes it twice). Make the test observable: resolve the client, run one `QueryAsync` so there is a resolved query, dispose the scope (two `Dispose` calls), then `PersistentStateHarness.PersistAsync(state)` and assert `written` is empty, that is, the subscription really was removed. Then state plainly, in the test name or the risk note, that idempotency itself is not observable from outside.
- **Phase:** A.
- **Risk:** Without the client resolved, the persisting callback would early-return anyway (`PrerenderTransfer.cs:128-130`), and "empty" would prove nothing. The client must be resolved and must hold data.

---

## RevalQuery.Blazor.Tests/PersistentStateHarness.cs (fixture)

It reflects into non-public members. Probe of the shipped assemblies (`Microsoft.AspNetCore.Components` 8.0.11, 9.0.5, 10.0.2, which are what the test bins resolve):

| Member used | net8 | net9 | net10 | Harness behaviour |
|---|---|---|---|---|
| internal ctor | `(IDictionary currentState, List<PersistComponentStateRegistration>)` | same | `(…, List<…>, List<RestoreComponentStateRegistration>)` | `.Single()` plus `Activator.CreateInstance` for params > 0. Survives net10 only because the new parameter happens to be a `List<>`. A future parameter of interface or logger type throws. |
| `InitializeExistingState` | `(IDictionary)` | same | `(IDictionary, RestoreContext)` | Passes **null** for `RestoreContext` on net10. It works today only because `TryTake` does not dereference it. That is undocumented and could become an NRE in a later version. |
| `PersistingState` (non-public setter) | yes | yes | yes | — |
| `_registeredCallbacks` (private field name) | yes | yes | yes | A rename gives an NRE (loud). A **semantic** move (callbacks stored elsewhere, field kept) makes `PersistAsync` run zero callbacks **silently**. |
| registration `Func<Task>` property | `Callback` | same | same | `OfType<Func<Task>>().Single()` |

It also bypasses real behaviour: render-mode filtering (`SupportsRenderMode` and inferred render modes), dispatcher marshalling, and the once-only guard. So it cannot catch a transfer that the real framework would skip on a given render mode.

Verdict: fragile across versions, and it has one **silent** failure mode that turns `A_Failed_Query_Is_Not_Transferred` and `Loading_Skips…` into passes. Replace it with the **public** API, which exists unchanged on net8, net9 and net10 (verified by metadata on all three and by a run on net8 and net10):
- `new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance)` (the logger-only ctor exists on all three).
- `await manager.RestoreStateAsync(store)` (public on all three; net10 adds a `RestoreContext` overload, which isn't needed), then `manager.State` is the `PersistentComponentState` to register.
- Persist with `await manager.PersistStateAsync(store, renderer)`. It needs a `Renderer`; `(Renderer)new Bunit.TestContext().Renderer` works (probe), so `PrerenderTransferTests` and `ReviewOf1784595Tests` hold a `TestContext` field or derive from it.
- `store` is a small `IPersistentComponentStateStore`: `GetPersistedStateAsync` returns the input dictionary, `PersistStateAsync(IReadOnlyDictionary)` captures the output, and `SupportsRenderMode` returns `true`.

Probe: the public path wrote `RevalQuery.PrerenderTransfer` on the server side, and a fresh manager restored it with `PrerenderTransfer.LoadAsync` returning the data on net8 and net10. API shape: `CreateEmptyAsync()` → `(manager, state)`, `PersistAsync(manager)` → written dictionary, `CreateFromAsync(dict)` → state. `PersistStateAsync` may be called once per manager, which matches every test (each persists once).

## RevalQuery.Blazor.Tests/Fx.cs (fixture)
- `Gates` hold raw `TaskCompletionSource` (with RCA). Switch to TestKit `Gate`. The handler awaits `gate.Task` without the token (`Fx.cs:42`), so pass `ctx.CancellationToken` (`gate.Task.WaitAsync(ct)`) so a failed test doesn't leave a handler parked forever.
- Don't convert it to `GatedHandler<T>`. Most tests depend on the ungated handler completing **synchronously** inside the render (ground truth 1), and a per-call gate would make every read async.
- `Fx` and `fx.Client` are registered as **instances** (`RevalRendererTests.cs:17-18`), so the container never disposes them, and `Fx.Dispose` is never called. There are no timers (RecordingEviction), so nothing leaks into later tests, but clean it up: register with factories, or dispose in the test class `Dispose`.
- No statics. `Calls` is per instance and is a `ConcurrentDictionary`. Fine.

## .razor fixtures
`Basic`, `Gated`, `Helper`, `Hidden`, `InChild`, `LateReader`, `Loop`, `Popover`, `Saver`, `Swapper`, `Wrapped`, `Wrapper`. None needs changing for determinism. `Saver.Handler` awaits `Gate` without the token (`Saver.razor:17`); pass `ctx.CancellationToken` for the same reason as `Fx`. `Popover`'s posted show (`Popover.razor:10`) is the deliberate "later batch", and the waits on it stay render-driven.

---

## Findings

**Suspected library issues**
1. **Dead, divergent `IMutationState`.** `RevalQuery.Core/Abstractions/Mutation/IMutationState.cs` is implemented by no type. `MutationState<,>` (`MutationState.cs:35`) exposes `IsException`/`IsResolved`/`Exception`, not the interface's `IsError`/`IsSuccess`/`Error`. It has been like this since `3e8a650` ("whole library refactor"). It is public API that cannot be used. This is also why TestKit `Signals` needs a `MutationState<,>` overload. It's a maintainer decision (implement it or delete it). It's not a test change.
2. (Low) **`PrerenderTransfer.Take`** (`PrerenderTransfer.cs:169-182`) catches `JsonException` only around the inner deserialize. A corrupt outer value makes `_state.TryTakeFromJson<string>` throw out of `LoadAsync`. Today `CompositeQueryPersistence` and `RevalClient.LoadPersistedAsync` both swallow it, so the user just sees a refetch. That outcome is fine, but the intent of the local `catch` (a corrupt payload counts as "nothing arrived") holds only by accident of the outer layers. The vacuous stale test was running exactly this path.

**Research-doc claims that are wrong or incomplete (for these files)**
- P6: "`RevalRendererTests.cs:220` has to drop to `UntilAsync` because the state … changes **without a render**". Wrong. The hidden `Saver` still re-renders on the mutation's final `NotifyChanged` (`RevalRenderer` forwards every change to `HandleEventAsync`, and `Show` only hides markup). Probe: `cut.WaitForState(() => state.IsResolved)` with `RenderCount` strictly increasing passed 300 out of 300 on net8 and net10. `Signals` is still the better choice because the predicate is library state, but it is not *required*.
- P6 "sound for what they do": true about mechanism, but the 1 s `DefaultWaitTimeout` decides pass/fail on a slow machine. It has to become `HangGuard` (bUnit-HG) to meet the maintainer's rule.
- P1 lists `PrerenderTransferTests.cs:284` as a P1 sleep. It is **P1a**: `QueryAsync` already awaits the save (`QueryWorker.cs:389`), so the sleep can simply go.
- Missing P4: `A_Failed_Query_Is_Not_Transferred` spends 14 s in real retry backoff on the server `QueryAsync` (default 3 retries with 2/4/8 s). This, not the client, is the slow part. The `timeoutMs: 15_000` at `:203` is unrelated to it.
- Recommendation A.5 "replace `UntilAsync` … with an `OnChanged`-based wait on the query … state" for `PrerenderTransferTests.cs:115, 172, 203`. The right signal there is `QueryState.RestoreCompleted`, which the library documents as ending *after* the follow-up decision has been made. An `OnChanged` predicate on data would be weaker (for example, `Data != null` cannot express "and no fetch followed").
- Topic 2 cites "Blazor `Gate()` in `RevalRendererTests.cs:208`". It is defined at `:12`; `:208` is a use.
- The brief's `Signals.UntilAsync(IMutationState, …)` cannot be applied to any real mutation state (Finding 1).

**Tests that cannot fail (or cannot fail for the reason they are named for)**
- `PrerenderTransferTests.Stale_Transferred_Data_Is_Shown_And_Then_Refetched`: the timestamp rewrite never matches and corrupts the payload, so the test passes whether or not stale transferred data is shown or refetched. "Shown" is never asserted at all.
- `ReviewOf1784595Tests.Disposing_The_Prerender_Transfer_Twice_Is_Harmless`: the guard it protects is unobservable, because the framework's unsubscribe is idempotent.
- `PrerenderTransferTests.The_Transfer_Joins_A_Persistence_Store_Rather_Than_Replacing_It`: only exercises transfer-then-store registration order, so it cannot detect a replace or `TryAdd` regression that affects store-then-transfer.
- Weak, not vacuous: `A_Query_That_Completes_During_The_Read…` (`InRange(1,2)` where exactly 2 is the claim). `A_Failed_Query_Is_Not_Transferred` and `Loading_Skips…` (an empty or `null` result is indistinguishable from "the harness persisted nothing").
- Duplicate: `A_Transfer_Without_An_Invalidation_Is_Reused` is the same as `A_Prerendered_Query_Reaches_The_Client_Without_A_Second_Fetch`.

**Counts**: 30 tests. 17 are unchanged or need only bUnit-HG (9 no-change and 7 bUnit-HG-only in RevalRendererTests, plus `Registration_Without…`). 13 need concrete fixes (3 in RevalRenderer, 7 in Prerender, 3 in Review; the Review ones are scope disposal, positive controls and the vacuous-dispose rewrite). All 30 are Phase A. 0 need phase B (fake time). 0 NEEDS-SIGNAL: `RestoreCompleted` and `MutationState.OnChanged` already exist. The only TestKit addition is the `MutationState<,>` overload of `Signals.UntilAsync`.
