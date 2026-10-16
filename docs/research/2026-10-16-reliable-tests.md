# Making the RevalQuery test suites deterministic

**Question.** How do we make `RevalQuery.Core.Tests` (xUnit 2.9.3) and `RevalQuery.Blazor.Tests` (xUnit 2.9.3 + bUnit 1.40.0) reliable, meaning they pass or fail for a reason in the code and not because of how fast the machine ran, without falling back on longer timeouts?

**Summary.** Most of the unreliability is in the tests, not in the library, and fixing it needs no library change. A probe run against the current code (see "Verified behaviour" below) shows that, with no persistence, `Subscribe` starts the fetch synchronously, and a handler that returns a completed task resolves the query before `Subscribe` returns. So a large share of the `await Task.Delay(…)` calls (for example the whole of `EnabledToggleTests`) can be replaced by immediate assertions. That is exactly how TanStack's own tests look: `observer.subscribe(); expect(count).toBe(1)`. The rest of the test-side work replaces sleeps with `TaskCompletionSource` gates that the test controls (handlers, persistence fakes), replaces the "poll until deadline, then return silently" helpers with waits on the library's own signals (`OnChanged`, `RestoreCompleted`, the returned `Task`), and makes every wait fail loudly. Four behaviours stay time-driven no matter what the tests do: `RefetchInterval` polling, retry backoff, `GcTime` eviction, and `StaleTime` expiry. The library reads `DateTime(Offset).UtcNow` and calls `Task.Delay` directly in five places, so no test can control them. TanStack makes these deterministic by swapping the global clock and timers (`vi.useFakeTimers()`, which also mocks `Date`). .NET has no global swap, so the equivalent is to inject `System.TimeProvider`, which is in the BCL on net8.0 and needs no package. Tests then drive it with `FakeTimeProvider`. That is a library change and needs an ADR. Its main design point is **not** "pass a TimeProvider to `Task.Delay`": the polling and GC loops arm their delay inside `Task.Run`, so the test can never know when the timer has been armed. The timers have to be armed synchronously (`TimeProvider.CreateTimer`), the way TanStack arms `setInterval` synchronously in `subscribe`. xUnit v3 is a useful follow-up (it removes the `AsyncTestSyncContext` that `FakeTimeProvider`'s README warns about, and adds `TestContext.Current.CancellationToken`) but it is not a prerequisite. Microsoft Coyote is effectively dormant (last release March 2024, net8.0 at most, an unanswered ".NET 9 support" issue), so it is not recommended.

---

## Verified behaviour of the current library (probe)

A throwaway console project in the session scratchpad, referencing `RevalQuery.Core` at the working tree, ran on net8.0:

```csharp
var opts = QueryOptions.Create<string>("k", _ => { calls++; return Task.FromResult("data"); }).Build();
var obs = client.Subscribe(opts, () => { });
// printed: sync after Subscribe: calls=1 data=data resolved=True
client.Invalidate("k");
// printed: sync after Invalidate: calls=2
```

This matches the code. `QueryWorker.RunAsync` (`RevalQuery.Core/Query/QueryWorker.cs:275-300`) calls `RunCoreAsync` without yielding. `RunFetchAsync` (`:335-360`) calls the handler through `ExponentialBackoffRetryPolicy`, whose first attempt (`ExponentialBackoffRetryPolicy.cs:29-41`) awaits nothing before `handler()`. A completed handler task therefore runs `ApplyFetched` inline. The exception is persistence: `RevalClient.cs:117-121` starts `LoadPersistedAsync` on `Task.Run`, so any test with an `IQueryPersistence` really is asynchronous, and must wait on `RestoreCompleted` or a gate instead of sleeping.

---

## Where the suites are unreliable today

Measured on the working tree (excluding `obj/`): 60 `Task.Delay` calls in tests. There are 86 call sites of polling helpers (`TestUtils.WaitUntilAsync`, two private `WaitUntil` copies, `TestUtils.WaitForStateAsync`, Blazor `TestUtils.UntilAsync`), and 39 `TimeSpan.From(Milli)Seconds` literals. The brief's figure of 66 bUnit `WaitForState`/`WaitForAssertion` calls did not reproduce: there are **10**, all in `RevalRendererTests.cs`. The brief's 74 polling-helper uses also did not match: I count 86 when the two private copies are included.

### P1. Sleep, then assert that work has finished (positive outcome)

The test sleeps for a fixed time and then asserts on state the library reaches asynchronously, or synchronously, as P1a shows. It passes only if the work finished within the sleep. That depends on thread-pool load, other test classes running in parallel (xUnit's default; see topic 4), and CI speed.

- `EnabledToggleTests.cs:47,52,70,96,101,107`: `Task.Delay(100/200)` before asserting `_calls`/`Data`.
- `RestoreBoundaryTests.cs:60` (`Task.Delay(800)` racing a 150 ms load plus a 150 ms handler), and `:88, :115, :138, :180`.
- `ReviewFindingsTests.cs:104, :125`, `StaticQueryTests.cs:56, :88, :105, :140`, `DisposalTests.cs:59`.
- `Blazor.Tests/PrerenderTransferTests.cs:85, :141, :250` (inside the `Restored` helper), `:284`.

**P1a, the sleep is not needed at all.** Where no persistence is configured and the handler is `Task.FromResult`, the outcome is already there when `Subscribe` or `Invalidate` returns (probe above). `EnabledToggleTests` (all three tests) is the clearest case. `RestoreBoundaryTests.cs:180` is another.

### P2. Sleep, then assert that something did *not* happen (negative outcome)

Waiting cannot prove an absence. The sleep only picks how long the test looks, and a late event after the sleep is missed, which is a false pass.

- `EnabledToggleTests.cs:74-79` (after disabling and invalidating, `_calls` stays 1).
- `PollingTests.cs:83` (`AQueryNobodyObservesDoesNotPoll`: 200 ms against a 50 ms interval).
- `StaticQueryTests.cs:56, :88`, `ReviewFindingsTests.cs:104` ("0 handler calls"), `ReviewFindingsTests.cs:145` and `ReviewOfA700df3Tests.cs:49` ("a late restore never overwrites").
- `CancellationTests.cs:35` (a 100 ms sleep after `IsIdle`, to "make sure" the dropped result never lands).

### P3. Timing used to force an interleaving (race tests)

The test makes A happen before B by making B sleep longer, and real-time scheduling decides whether that holds.

- `ReviewFindingsTests.cs:47-53`: the handler sleeps 30 ms, the test sleeps 5 ms, and then a second `QueryAsync` must land "mid-fetch".
- `IssueReproductionTests.cs:246-260`: mutation `SLOW` (300 ms) and `FAST` (10 ms) with a 50 ms sleep between them.
- `ReviewOfA700df3Tests.cs:80-90`: a 30 ms restore must land during an 80 ms failing fetch.
- `RestoreBoundaryTests.cs:14-19` (and the `StubPersistence`/`SlowPersistence` fakes): the persistence latency is `Task.Delay(delay)`.
- `CancellationTests.cs:23, 51, 75, 102`: a handler `Task.Delay(200)` keeps a fetch "in flight" while the test cancels. The `started` TCS gates the start, but the end is still a 200 ms timer. `CancellationTests.cs:114` then polls for the abandoned fetch to land.
- `IssueReproductionTests.cs:226-228` (`SlowHandler` 150 ms) in the stampede test: 16 callers must all join a fetch that is still in flight within 150 ms.

### P4. Wall-clock-driven library behaviour

The library uses real time internally, so the test can only wait it out.

- Polling: `PollingTests.cs:25, :57, :75, :98` (30-50 ms intervals). The library runs `Task.Run` plus a `Task.Delay(interval)` loop (`QueryWorker.cs:212-219`).
- GC: `IssueReproductionTests.cs:98-110` (`GcInterval` 20 ms, then poll until evicted). The library does `DateTime.UtcNow` expiry plus a `Task.Delay(GcInterval)` loop (`TtlQueryGarbageCollector.cs:42, 120, 151`).
- Staleness: `QueryState.cs:293, 309` (`DateTimeOffset.UtcNow`). Tests side-step this with huge margins (`StaleTime` of 10 minutes or 1 hour, `UtcNow.AddHours(-3)` in `ReviewFindingsTests.cs:113, :133`) or with `StaleTime(TimeSpan.Zero)`. That is robust in practice, but it still reads the real clock, and `ReviewFindingsTests.cs:131` asserts `LastUpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1)`.
- Retry backoff: `ExponentialBackoffRetryPolicy.cs:37` (`Task.Delay(delay, ct)`). No test can check the *delays* deterministically today.

### P5. Polling helpers that hide failures

- `TestUtils.WaitUntilAsync` (`Core.Tests/TestUtils.cs:7-11`) polls every 10 ms until a deadline and then **returns silently**. The two private copies do the same: `ReviewFindingsTests.cs:175-179` (5 ms) and `IssueReproductionTests.cs:348-352`. A wait that timed out is then reported by whatever assertion comes next, or not at all. `PollingTests.cs:104-106` asserts the same predicate it waited on, so a timeout surfaces as "expected ≥ 3". `EvictionRaceTests.cs:58` waits up to 1000 ms per round and then asserts.
- `TestUtils.WaitForStateAsync` (`TestUtils.cs:27-43`) is an event-driven wait and fails loudly (a `TimeoutException` from `WaitAsync`), but it has two defects:
  1. **A lost-wakeup race.** It checks the predicate (`:29`) *before* subscribing to `OnChanged` (`:34`) and never checks again after subscribing. A change landing between the two is missed, and the wait then hangs until the timeout.
  2. **No `RunContinuationsAsynchronously`.** `new TaskCompletionSource<bool>()` (`:31`) means `TrySetResult` runs the rest of the test inline, on the library thread that raised `OnChanged`, inside the library's notify call. The test then asserts while the library is still mid-notification.
- Blazor `TestUtils.UntilAsync` (`RevalRendererTests.cs:385-393`) fails loudly (`ThrowIfCancellationRequested`) but spins `Task.Yield()` hot for up to 10-15 s (`PrerenderTransferTests.cs:203` passes `timeoutMs: 15_000`).

### P6. bUnit waits

`RevalRendererTests.cs:40, 69, 134, 149, 178, 214, 237, 279` (`WaitForAssertion`) and `:339, :344` (`WaitForState`). These re-check on every render and fail loudly after `TestContext.DefaultWaitTimeout` (1 s; see topic 3). So their only flake mode is "the render needed more than 1 s". They are sound for what they do. `RevalRendererTests.cs:220` has to drop to `UntilAsync` because the state it waits on (`state.IsResolved` while the component is hidden) changes **without a render**, and bUnit cannot see such a change.

### P7. Shared mutable statics and clock-derived identity

- `static int _calls` (`EnabledToggleTests.cs:13`, `StaticQueryTests.cs:11`), `_serverCalls`/`_clientCalls` (`PrerenderTransferTests.cs:24-25`) and `SlowHandlerCalls` (`IssueReproductionTests.cs:222`). xUnit runs the tests of one class sequentially (topic 4), but a background fetch, poll or restore left over from the previous test can still increment the counter after the next test resets it. Each test should own its counter.
- `PollingTests.cs:110` and `QueryAsyncIntegrationTests.cs:84` make "new data" with `DateTime.UtcNow.Ticks`. Distinctness then depends on clock resolution. A per-test `Interlocked.Increment` counter makes it exact.

### P8. Hang guards

`WaitAsync(TimeSpan.FromSeconds(5))` (`CancellationTests.cs:169-262`, `OrphanedFetchTests.cs:60, :68`) and `started.Wait(TimeSpan.FromSeconds(2))` (`CancellationTests.cs:82`) do not decide the outcome. They turn a hang into a failure. They are not a timing heuristic in the P1-P4 sense, but they do fail a test that is only slow. See open question 3.

### P9. Weak or vacuous tests found while classifying

- `PollingTests.SubscribeThenDispose_PollingStops` (`PollingTests.cs:52-67`) asserts the data straight after `Dispose()`. It never checks that polling stopped, so it cannot fail.
- `CancellationTests.cs:23` and similar use a handler that ignores its token (`Task.Delay(200)` with no `ct`), so the 200 ms really elapses and keeps work running after the test.

---

## Topic 1: `System.TimeProvider` and `FakeTimeProvider`

**Availability.**
- `TimeProvider` is "Included in the framework" on .NET 8+, and comes from the `Microsoft.Bcl.TimeProvider` package only for .NET 5-7, .NET Framework 4.6.2+ and .NET Standard 2.0. [TimeProvider overview](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)
- So Core can take a `TimeProvider` on net8.0/net9.0/net10.0 with **no new package reference**, which keeps the "Core depends only on `*.Abstractions`" convention (`docs/CONVENTIONS.md` #2).
- The motivation recorded in the API proposal includes "the creation of tests that can mock time functionality". [dotnet/runtime#36617](https://github.com/dotnet/runtime/issues/36617)

**Overloads that take a TimeProvider, all on net8.0:**
- `Task.Delay(TimeSpan, TimeProvider)` and `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`, which "Applies to" net-8.0 through net-11.0. [Task.Delay](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-8.0)
- `Task.WaitAsync(TimeSpan, TimeProvider[, CancellationToken])` and `CancellationTokenSource(TimeSpan, TimeProvider)`. [TimeProvider overview, "Use with .NET"](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)
- `PeriodicTimer(TimeSpan, TimeProvider)`, which applies to net-8.0 and later. [PeriodicTimer ctor](https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer.-ctor?view=net-8.0)
- `TimeProvider.CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)` returns an `ITimer`. [TimeProvider overview](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)

**How `Task.Delay(…, timeProvider)` behaves with a non-system provider (net8 source).**
- For a provider other than `TimeProvider.System`, `DelayPromise` calls `timeProvider.CreateTimer(…)` under `ExecutionContext.SuppressFlow()`. [Task.cs L5698-L5730, release/8.0](https://github.com/dotnet/runtime/blob/123e7aa4d4ada32bf0ac2ea0f20dcc0710f70d05/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L5698-L5730)
- The runtime deliberately does not set `RunContinuationsAsynchronously` on timer completion: "we want the timer firing … to synchronously invoke any continuations from this task". It sets the flag only on the cancellation path. [Task.cs L5765-L5776](https://github.com/dotnet/runtime/blob/123e7aa4d4ada32bf0ac2ea0f20dcc0710f70d05/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L5765-L5776)
- So with a fake provider, the awaiting continuation runs **inline inside `FakeTimeProvider.Advance`**, unless a `SynchronizationContext` or a scheduler was captured. That case is covered under the xUnit v2 pitfall below.

**`FakeTimeProvider` (package `Microsoft.Extensions.TimeProvider.Testing`, current 10.11.0 on NuGet).**
- On net8.0/net9.0/net10.0 it has **no dependencies**. Only net462 pulls `Microsoft.Bcl.TimeProvider`. [csproj](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/Microsoft.Extensions.TimeProvider.Testing.csproj), [nuspec 10.11.0](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.timeprovider.testing/10.11.0/microsoft.extensions.timeprovider.testing.nuspec). It would be a test-project-only reference.
- The default start is midnight 1 January 2000. [FakeTimeProvider.cs L19, L27](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L19-L30)
- `AutoAdvanceAmount` is "the amount of time by which time advances whenever the clock is read", and defaults to zero. [L56-L62](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L56-L83) It advances only on a read, so a bare `Task.Delay` never completes on its own. That was reported as a bug and closed as by-design. [dotnet/extensions#6680](https://github.com/dotnet/extensions/issues/6680)
- `Advance(delta)` and `SetUtcNow` move `_now` under a lock and then call `WakeWaiters()`. [L98-L134](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L98-L134)
- `WakeWaiters` **runs timer callbacks synchronously on the caller's thread**, earliest wake-time first, ties broken by scheduling order. It reschedules periodic timers with `WakeupTime += Period` and loops, so one `Advance(3 × period)` fires a periodic `CreateTimer` callback three times. [L231-L311](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L231-L311)
- `AdjustTime` changes the clock "and has no impact on outstanding timers". [L136-L158](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L136-L158)
- `TimestampFrequency` is `TimeSpan.TicksPerSecond`. [L193](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs#L193)

**Pitfalls (first-party issues).**
1. **xUnit v2's sync context.** The package README says xUnit v2's `AsyncTestSyncContext` "may lose track of the continuation, causing the test to become unresponsive" when the code under test uses `ConfigureAwait(false)` with `FakeTimeProvider`. It recommends `SynchronizationContext.SetSynchronizationContext(null)` in such tests, and states that in xUnit v3 "`AsyncTestSyncContext` has been removed … so above issue is no longer a problem". [README](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/README.md) The related Polly report was resolved by continuing on the captured context. [dotnet/extensions#4912](https://github.com/dotnet/extensions/issues/4912) (The exact mechanism is the README's claim; I did not reproduce it.)
2. **Continuations scheduled asynchronously do not run inside `Advance`.** `.ContinueWith(…)` without `ExecuteSynchronously` was not run by `Advance`. That was closed as not-a-bug: the continuation is "scheduled to run asynchronously (default)". [dotnet/extensions#5326](https://github.com/dotnet/extensions/issues/5326) The same applies to any work after an `await` that hopped to the thread pool, such as the body of `Task.Run`.
3. **No way to await what `Advance` started.** An open proposal asks for `Task Advance(...)`, because once a timer callback "hits an await keyword, the thread jumps to the next waiter … there is no reliable way to wait for their completion". [dotnet/extensions#6404](https://github.com/dotnet/extensions/issues/6404)
4. **No way to observe whether a timer is armed.** An open proposal asks to expose the active timers, because tests can otherwise only assert indirectly. [dotnet/extensions#7515](https://github.com/dotnet/extensions/issues/7515) For RevalQuery this is the decisive point: the polling loop `Task.Run(async () => { await Task.Delay(interval, …) })` (`QueryWorker.cs:212-219`) and the GC loop (`TtlQueryGarbageCollector.cs:141, 151`) arm their delay at an unknown later moment. An `Advance` that arrives before the arm is lost, because the delay is then measured from the already-advanced clock. Changing only `Task.Delay(interval)` to `Task.Delay(interval, tp)` would therefore **not** make polling deterministic.
5. **Timers that reschedule themselves.** A one-shot timer that calls `Change` from its own callback fires only once per `Advance`, even if the advance covers several periods (open). [dotnet/extensions#5722](https://github.com/dotnet/extensions/issues/5722) Calling `Advance` from inside a callback does not fire further ticks. That is intentional since PR #4277, and a comment notes it prevents testing `PeriodicTimer` this way (open). [dotnet/extensions#6810](https://github.com/dotnet/extensions/issues/6810) So `PeriodicTimer` is the wrong primitive to drive with `Advance` here. A periodic `CreateTimer` is the right one: its `WakeWaiters` loop handles multi-period advances.
6. The `ITimer` from `FakeTimeProvider.CreateTimer` is not implicitly rooted, unlike the system one, so the library must keep a reference to its timers (open). [dotnet/extensions#5765](https://github.com/dotnet/extensions/issues/5765)

## Topic 2: Deterministic async test patterns

- **Gates under test control.** bUnit's own documented pattern for async components is a `TaskCompletionSource<string>` standing in for the service, with the test calling `SetResult`. [bUnit "Awaiting an asynchronous state change"](https://bunit.dev/docs/interaction/awaiting-async-state.html) The suites already use this in places (`CancellationTests.cs:17` `started`, `OrphanedFetchTests.cs:52-53` `started`/`release`, Blazor `Gate()` in `RevalRendererTests.cs:208`). The fix for P3 is to put a gate at **both** ends of every race: handler start, handler finish, and persistence `LoadAsync`. The order is then whatever order the test releases the gates in.
- **`RunContinuationsAsynchronously`.** It "Forces continuations added to the current task to be executed asynchronously". [TaskCreationOptions](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcreationoptions?view=net-8.0) Without it, `TrySetResult` runs the waiter's continuation inline on the signalling thread. The library already does this for its own completion sources (`QueryWorker.cs:292`), and bUnit does it in its wait helper (`WaitForHelper.cs:61`, [v1.40.0](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/WaitForHelper.cs#L61)). Test gates and `WaitForStateAsync` should follow suit.
- **Subscribe, then check.** bUnit's helper subscribes and runs the initial check together, inside the renderer's context, "to ensure no renders happens between the initial OnAfterRender and subscribing". [WaitForHelper.cs L126-L143](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/WaitForHelper.cs#L126-L143) `TestUtils.WaitForStateAsync` checks before it subscribes and never again. The fix is to subscribe first and then check, as bUnit's comment describes.
- **Await the library's signal, not a predicate.** Core already exposes `QueryState.OnChanged` (`QueryState.cs:111`), `RestoreCompleted` (`:195`), `MutationState.OnChanged` (`MutationState.cs:84`), and the `Task` returned by `QueryAsync`, `ExecuteAsync` and `CancelAsync`. `CancelAsync` already "completes only once the fetch has unwound" (`CancellationTests.cs:42-59`). Every P1 case with persistence can await `RestoreCompleted`, then the fetch it starts, instead of sleeping.
- **Why "poll until deadline, then return silently" hides failures.** A good unit test is "Repeatable" and "Self-Checking: The test should automatically detect if it passed or failed". [Microsoft Learn, unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices) A wait that times out silently makes the timeout invisible: the next assertion fails with a misleading message, or passes because it does not test the waited-for condition. It also turns "the machine was slow" into "the code is wrong", and the reverse, depending on what follows.
- **Negative assertions (P2).** You cannot wait deterministically for something to not happen. The deterministic forms are:
  - The decision is synchronous, so assert right after the call (`Invalidate` decides synchronously; see the probe).
  - Time is fake, so advance past the point where it *would* have happened and then assert. This is TanStack's form, e.g. `queryObserver.test.tsx` L892-L894 below.
  - A quiescence signal from the library says "nothing is scheduled".

  Otherwise, document the limitation.

## Topic 3: bUnit 1.x waits

- `WaitForState`/`WaitForAssertion` run the check once when called and then **after every render**. If the check has not passed when the timeout timer fires, the wait fails with `WaitForFailedException`. [WaitForHelper.cs L63-L93, L163-L184](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/WaitForHelper.cs#L63-L93). The docs say the same: "will try the predicate passed to it when the `WaitForState()` method is called, and every time the component under test renders". [Awaiting async state](https://bunit.dev/docs/interaction/awaiting-async-state.html), [Async assertion](https://bunit.dev/docs/verification/async-assertion.html)
- The default timeout is `TestContextBase.DefaultWaitTimeout = TimeSpan.FromSeconds(1)` (static, settable). [TestContextBase.cs L17](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/TestContextBase.cs#L17) It becomes infinite when `Debugger.IsAttached`. [WaitForHelper.cs L214-L219](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/WaitForHelper.cs#L214-L219)
- bUnit's guidance: a component that renders asynchronously makes a test that "sometimes pass, and sometimes fail" unless you use these helpers. The wait predicate and the assertion "should not verify the same thing". Lowering the timeout does "_not_ make tests run faster". [docs](https://bunit.dev/docs/interaction/awaiting-async-state.html)
- The synchronous versions block the test thread (`waiter.WaitTask.GetAwaiter().GetResult()`, [RenderedFragmentWaitForHelperExtensions.cs L26-L32](https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/RenderedFragmentWaitForHelperExtensions.cs#L26-L32)). The `…Async` versions are `internal` in 1.40 (`L51`, `L94`).
- **Consequence for RevalQuery.** The WaitFor helpers are event-driven (by renders), not polling, so for state that ends in a render they are already deterministic, apart from the 1 s ceiling. They cannot observe a state change that does not render (P6, `RevalRendererTests.cs:220`). For those, wait on the library's `OnChanged`.
- **bUnit 2.x** (2.0 released 2025-11-11; current 2.11.3 on NuGet) renames `TestContext` to `BunitContext` and unifies `Render` methods. [1→2 migration](https://github.com/bUnit-dev/bUnit/blob/v2.9.0/docs/site/docs/migrations/1to2.md) It makes `WaitForStateAsync`/`WaitForAssertionAsync` public. [RenderedComponentWaitForHelperExtensions.WaitForState.cs L52, L121](https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/Extensions/WaitForHelpers/RenderedComponentWaitForHelperExtensions.WaitForState.cs#L52) The model is unchanged: still a real `System.Threading.Timer` with a 1 s default ([WaitForHelper.cs L99](https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/Extensions/WaitForHelpers/WaitForHelper.cs#L99), [BunitContext.cs L23](https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/BunitContext.cs#L23)), and no `TimeProvider` support (no occurrence in `src/bunit` at v2.9.0). It targets net8.0-net11.0. [bunit.csproj L3](https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/bunit.csproj#L3) Moving to 2.x is a separate API migration and does not by itself improve determinism.

## Topic 4: xUnit 2.9 vs v3

- **Parallelism (both versions).** "By default, there is a test collection per test class". Collections run in parallel and tests inside one run sequentially. Max parallel threads defaults to the number of CPU threads. It can be disabled with `[assembly: CollectionBehavior(DisableTestParallelization = true)]`, `[CollectionDefinition(..., DisableParallelization = true)]`, the console's `-parallel none`, or the runner configuration. [Running tests in parallel](https://xunit.net/docs/running-tests-in-parallel) For RevalQuery this means every P1/P3 test is competing for the thread pool with every other test class. `EvictionRaceTests` already sits in a non-parallel `"stress"` collection (`EvictionRaceTests.cs:13-17`). Turning parallelism off makes P1-P4 *rarer* but not deterministic, so by the maintainer's rule it is not a fix.
- **v2 `Timeout`.** "Using this with parallelization turned on will result in undefined behavior. Timeout is only supported when parallelization is disabled". [xunit v2 FactAttribute.cs L26-L32](https://github.com/xunit/xunit/blob/055b428274f62130f0d91d3c7e8aab7fb8699b0a/src/xunit.core/FactAttribute.cs#L26-L32) In v2 it also needed an async test. [What's new in v3](https://xunit.net/docs/getting-started/v3/whats-new)
- **v3 changes that bear on reliability.** [What's new in v3](https://xunit.net/docs/getting-started/v3/whats-new)
  - `TestContext.Current.CancellationToken` is signalled when a test times out ("A test which has timed out does not stop running forcefully; rather, the cancellation token … will be signaled") or is cancelled.
  - `Timeout` works for non-async tests.
  - `Explicit` tests are "skipped by default unless explicit tests are requested". [FactAttributeBase_aot.cs L32-L37](https://github.com/xunit/xunit/blob/e91cdbe7557649b1179fbc47b01e8d3a615d1256/src/xunit.v3.core.aot/Internal/FactAttributeBase_aot.cs#L32-L37)
  - `Assert.Skip`/`SkipUnless`/`SkipWhen`.
  - `CollectionBehavior.ParallelAlgorithm`. Under `Aggressive`, timeouts are undefined, and timing "only reliable when using `ParallelAlgorithm.Conservative`" (the default). [FactAttributeBase_aot.cs L97-L105](https://github.com/xunit/xunit/blob/e91cdbe7557649b1179fbc47b01e8d3a615d1256/src/xunit.v3.core.aot/Internal/FactAttributeBase_aot.cs#L97-L105), [parallel docs](https://xunit.net/docs/running-tests-in-parallel)
  - Most relevant to topic 1: `AsyncTestSyncContext` is removed. [Migration guide](https://xunit.net/docs/getting-started/v3/migration), [FakeTimeProvider README](https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/README.md)
  - xUnit has **no built-in retry** in v2 or v3 (I found none in the docs read). Under the maintainer's constraints retries would be a heuristic anyway.
- **Migration cost.** [Migration guide](https://xunit.net/docs/getting-started/v3/migration)
  - Packages become `xunit.v3` (now 4.0.2 on NuGet, requiring ".NET Framework 4.7.2 or later, and .NET 8 or later"; [nuspec](https://api.nuget.org/v3-flatcontainer/xunit.v3/4.0.2/xunit.v3.nuspec)).
  - `OutputType` changes to `Exe`.
  - `IAsyncLifetime` moves to `ValueTask` and inherits `IAsyncDisposable`.
  - `async void` tests fail fast.

  Neither suite uses `IAsyncLifetime` (not checked exhaustively; inferred from the files read), so the cost is mostly the csproj and package changes plus checking bUnit compatibility. bUnit 1.40 with xUnit v3 is **(unverified)**.

## Topic 5: Systematic concurrency testing (Coyote and alternatives)

- **Microsoft Coyote.** The last NuGet release is 1.7.11, published 2024-03-18. Its target frameworks top out at net8.0 (netstandard2.0, netcoreapp3.1, net6.0, net8.0). [nuspec](https://api.nuget.org/v3-flatcontainer/microsoft.coyote.test/1.7.11/microsoft.coyote.test.nuspec) The repository is not archived and has had small commits in 2026 (e.g. 2026-10-09, #510, #520), but no release since. The ".NET 9 support" issue (opened 2025-08-18) has no maintainer reply. Commenters call the project "abandoned since 2024", and one has published a third-party fork (not evaluated, not a primary source). [microsoft/coyote#521](https://github.com/microsoft/coyote/issues/521) A rewriter failure report from 2024-06 is still open. [microsoft/coyote#506](https://github.com/microsoft/coyote/issues/506) Coyote works by rewriting the test binaries, and RevalQuery tests run on net9.0 and net10.0 (`docs/CONVENTIONS.md` #3). **Conclusion: dormant for our purposes. Do not adopt.**
- **Lighter, deterministic alternatives that fit this codebase:**
  1. Interleavings forced by gates (topic 2). Each race in P3 becomes one test per interesting ordering, with the order set by which gate the test releases first.
  2. A **test-only eviction policy**. `RevalClient` already accepts `ICacheEvictionPolicy` (`RevalClient.cs:49, 57`), and `EvictionRaceTests` uses `EvictOnRelease`. That lets a test evict at an exact point instead of after `GcInterval`.
  3. Keep stress tests (`EvictionRaceTests`, `IssueReproductionTests` Barrier storms) as probabilistic regression nets, clearly labelled as such. They can only find a race, never prove its absence. That is a documented limitation, not a heuristic in the pass/fail logic, provided their inner waits fail loudly (today `EvictionRaceTests.cs:58` uses the silent `WaitUntilAsync`).

## Topic 6: How TanStack Query tests the same behaviours

All citations are at TanStack/query commit [`ee47993`](https://github.com/TanStack/query/tree/ee47993724e5b71e65d9f38ae416367d2dd18395).

- **Fake timers everywhere, no polling waits.**
  - `queryObserver.test.tsx` calls `vi.useFakeTimers()` in `beforeEach` and `vi.useRealTimers()` in `afterEach`. [L16-L25](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/queryObserver.test.tsx#L16-L25)
  - 16 of the query-core test files call `useFakeTimers`. There are **zero** `waitFor(` calls in `packages/query-core/src/__tests__` and `packages/react-query/src/__tests__` (my grep).
  - TanStack is actively removing the rest: "Removes the `waitFor` … polling helpers … The `lit-query` tests now run under fake timers" ([#11676](https://github.com/TanStack/query/pull/11676), merged 2026-09-26). It also replaced `await Promise.resolve()` with `vi.advanceTimersByTimeAsync(0)`, keeping waits "in front of assertions that check that something did not happen" ([#11744](https://github.com/TanStack/query/pull/11744)).
  - Their advance amounts are set to "the time the awaited work takes" ([#11657](https://github.com/TanStack/query/pull/11657), [#11460](https://github.com/TanStack/query/pull/11460)). They are exact, not padded.
- **`sleep` is a timer, so it is under fake time.** `export const sleep = (ms) => new Promise((resolve) => { setTimeout(resolve, ms) })`, documented as "e.g. to delay a `queryFn` under fake timers". [query-test-utils/src/sleep.ts](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-test-utils/src/sleep.ts) A handler's `sleep(20)` takes zero wall time and completes exactly when the test advances 20 ms. This is the analogue of a RevalQuery handler doing `Task.Delay(20, timeProvider)`.
- **refetchInterval.** The test subscribes, then `expect(count).toBe(1)` **synchronously**, then `await vi.advanceTimersByTimeAsync(10); expect(count).toBe(2)`. After `unsubscribe()` it advances another 10 and asserts the count is still 2 and the query was removed (`gcTime: 0`). [queryObserver.test.tsx L872-L895](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/queryObserver.test.tsx#L872-L895) The library side arms `setInterval` synchronously when the observer updates its interval, through `timeoutManager.setInterval`. [queryObserver.ts L553](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/queryObserver.ts#L553)
- **gcTime.** Each query arms its own `timeoutManager.setTimeout` on removal eligibility. [removable.ts L31](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/removable.ts#L31) The tests advance past `gcTime` and assert removal, including "should be garbage collected later when unsubscribed and query is fetching". [query.test.tsx L645-L663](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/query.test.tsx#L645-L663) RevalQuery uses a periodic sweep instead (`TtlQueryGarbageCollector`), so eviction happens between `GcTime` and `GcTime + GcInterval` after release. A fake-clock test has to advance by both.
- **retry / retryDelay.** The test sets `retry: 3, retryDelay: 1`, advances 1 ms, asserts there is no result yet, and then advances 2 ms and asserts `'data3'`. [query.test.tsx L111-L156](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/query.test.tsx#L111-L156) `retryer.test.tsx` also runs under fake timers. [L6-L17](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/retryer.test.tsx#L6-L17)
- **staleTime.** Staleness is computed from `Date.now()` ([utils.ts L158](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/utils.ts#L158)). `vi.useFakeTimers()` mocks `Date` by default, together with `setTimeout`, `setInterval` and the rest. [Vitest `vi` API](https://vitest.dev/api/vi.html) The observer also arms a stale timeout ([queryObserver.ts L527](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/queryObserver.ts#L527)). So one fake clock controls both "now" and "when". This is the property RevalQuery lacks while `QueryState.IsStale` reads `DateTimeOffset.UtcNow` but the poll loop uses `Task.Delay`.
- **Cancellation and races.** Cancellation tests start a query whose `queryFn` awaits `sleep(10)`, call `query.cancel()`, then `advanceTimersByTimeAsync(100)` and assert `signal.aborted` and `CancelledError`. [query.test.tsx L346-L394](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/query.test.tsx#L346-L394) Races are written as different `sleep` lengths under fake time, so the interleaving is fully determined by the virtual clock.
- **`timeoutManager`.** TanStack added a pluggable timer backend, but its stated purpose is performance (coalescing thousands of timers, delays beyond ~24 days), not testing. [timeoutManager.ts L19-L27, L71-L81](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/timeoutManager.ts#L71-L81) The default provider wraps the globals so that tests that "spy on or replace the global setTimeout" still work. [L52-L62](https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/timeoutManager.ts#L51-L69) Tests also spy on it directly (`queryObserver.test.tsx` L1389-L1390).
- **Why this does not port as-is.** `vi.advanceTimersByTimeAsync` "will include asynchronously set timers" ([Vitest](https://vitest.dev/api/vi.html)), and JS has one thread, so advancing time plus flushing promises reaches quiescence. .NET has no global timer or clock to patch, and continuations can run on the thread pool. The equivalents are: (a) an injected `TimeProvider`; (b) timers armed synchronously so that `Advance` sees them; (c) synchronous work after the timer fires, or a signal to await (topic 1, pitfalls 2-4).

## Topic 7: First-party Microsoft guidance

- "Characteristics of good unit tests: … **Repeatable** … **Self-Checking**." "Handle stub static references with seams": the worked example is `DateTime.Now`, and the prescribed fix is to "introduce a *seam* into your production code", so that "the test suite has full control over the `DateTime.Now` value". [Unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices) The page's seam is a hand-written interface. Since .NET 8 the BCL seam for this is `TimeProvider`, whose stated purpose is that "By using `TimeProvider`, you ensure that your code is testable and predictable". [TimeProvider overview](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)
- `Task.Delay` docs note that resolution is "approximately 15 milliseconds on Windows". The `Delay(TimeSpan, CancellationToken)` example itself "includes a potential race condition: it depends on the task asynchronously executing the delay when the token is cancelled". [Task.Delay](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-8.0) This is relevant to the 2-30 ms delays in P3.
- I looked for written dotnet/runtime test conventions on flaky or timing tests and **found none I could cite** (GitHub code search returned nothing for this session). Treat any claim about runtime's internal practice as **(unverified)**.

---

## Recommendations

Ordered so that each step stands alone and the earlier ones need no ADR.

### A. Test-only changes (no library change, no ADR)

1. **Make every wait fail loudly, and make the event wait correct.** (P5)
   - Delete `TestUtils.WaitUntilAsync` and the two private `WaitUntil` copies (`ReviewFindingsTests.cs:175`, `IssueReproductionTests.cs:348`).
   - Fix `WaitForStateAsync`: subscribe to `OnChanged` **before** the first check, check again after subscribing, and create the TCS with `TaskCreationOptions.RunContinuationsAsynchronously`.
   - Where a test truly has no event to wait on, the helper must throw on its guard (as Blazor `UntilAsync` does), never return.
2. **Remove sleeps that are not needed (P1a).** Where there is no persistence and the handler is synchronous, assert straight after `Subscribe`/`Invalidate`/`Run`, as TanStack does (`expect(count).toBe(1)` straight after `subscribe`).
   - `EnabledToggleTests` becomes sleep-free, including its negative assertions (P2): the enable/disable/invalidate decision is synchronous.
   - Same for `StaticQueryTests.cs:56, :88`, `RestoreBoundaryTests.cs:180`, `DisposalTests.cs:59`.
3. **Replace latency sleeps in fakes with gates (P3, P1 with persistence).**
   - The `Store`/`StubPersistence`/`SlowPersistence` fakes take a `TaskCompletionSource` (`RunContinuationsAsynchronously`) instead of a `delay` (`RestoreBoundaryTests.cs:14-19`, `ReviewFindingsTests.cs:185`, `ReviewOfA700df3Tests.cs:102`).
   - Handlers take `started`/`release` gates instead of `Task.Delay(n)` (`CancellationTests.cs:23, 51, 75, 102`; `ReviewFindingsTests.cs:47`; `IssueReproductionTests.cs:227, 246`; `ReviewOfA700df3Tests.cs:87`; `RestoreBoundaryTests.cs:54, :76`).
   - The test then releases the gates in the order the scenario names, and awaits `RestoreCompleted`, the returned fetch `Task`, or `OnChanged`.
   - The stampede test (`IssueReproductionTests.cs:203-219`) holds the handler on a gate until all 16 callers have called `QueryAsync`, then releases it and asserts one call.
4. **Negative assertions that are only async because of persistence (P2).** Gate the restore, release it, await `RestoreCompleted` and the follow-up fetch (or the "no fetch" decision), then assert. `ReviewFindingsTests.cs:145` and `ReviewOfA700df3Tests.cs:49` ("a late restore never overwrites") become: hold the load gate, let the fetch land, release the load, await `RestoreCompleted`, then assert. Where a negative outcome still has no deterministic signal after this, write that down as a limitation in the test (open question 2) instead of sleeping.
5. **Blazor.** Keep `WaitForAssertion`/`WaitForState` for anything that ends in a render (they are render-driven; topic 3). Replace `UntilAsync` (`RevalRendererTests.cs:220`, `PrerenderTransferTests.cs:115, 172, 203`) with an `OnChanged`-based wait on the query or mutation state, and replace `PrerenderTransferTests.cs:85, 141, 250, 284` sleeps the same way as in steps 3-4.
6. **Per-test state (P7).** Replace static counters with locals captured by the handler. Replace `DateTime.UtcNow.Ticks` "unique data" with an `Interlocked.Increment` counter.
7. **Fix vacuous tests (P9).** `SubscribeThenDispose_PollingStops` cannot fail today. Rewrite it after step B, under fake time: dispose, advance by several intervals, and assert the handler count did not move.
8. **Leave the stress tests in their non-parallel collection, but make their inner waits loud (topic 5).** Do not adopt Coyote.

### B. Library change: inject `TimeProvider` (needs an ADR)

This is the only way to make P4 (polling, retry delay, GC, staleness) deterministic. TanStack gets the same effect from global fake timers.

**Options.**
- **(a)** Do nothing in the library. Document polling, backoff, GC and staleness tests as time-dependent integration tests, kept in a separate, non-parallel collection. By the maintainer's rule this is only acceptable as a *documented limitation*. It is not a fix.
- **(b)** Thread a `TimeProvider` through. `GetUtcNow()` replaces `DateTime(Offset).UtcNow` in `QueryState.cs:293, 309` and `TtlQueryGarbageCollector.cs:42, 120`. `Task.Delay(delay, timeProvider, ct)` replaces `ExponentialBackoffRetryPolicy.cs:37`. The polling loop (`QueryWorker.cs:212-219`) and the GC loop (`TtlQueryGarbageCollector.cs:141-151`) keep `Task.Run` plus `Task.Delay(…, timeProvider)`. Small diff, but **not deterministic**, because the delay is armed at an unknown time after `Task.Run` (topic 1, pitfall 4).
- **(c)** As in (b), but **arm timers synchronously** with `timeProvider.CreateTimer(callback, null, interval, interval)`:
  - Create the timer under `_gate` in `StartPolling`, keep the `ITimer` in a field (pitfall 6), and dispose it where `_pollingCts` is cancelled today. The callback calls `RunIfAllowed()`, which already starts the fetch synchronously.
  - Do the same for the GC sweep in `EnsureStarted`. Alternatively use per-key timers as TanStack's `removable.ts` does, which removes the `GcInterval` approximation. That is a bigger change and its own decision.
  - Under `FakeTimeProvider`, `Advance(interval)` then fires the poll inline and `Advance(3 × interval)` fires it three times (`WakeWaiters` loop). With a synchronous handler, the test reads exactly like TanStack's `queryObserver.test.tsx` L872-L895. Retry stays `Task.Delay(delay, timeProvider, ct)`: its continuation runs inline on `Advance` when no sync context was captured (runtime `Task.cs` L5765-L5776). It is armed synchronously in the awaiting code, so the attempt that just failed has already armed it before the test advances.

**Recommendation: (c).**
- **Where the provider comes from.** Add `RevalQueryOptions.TimeProvider` (default `TimeProvider.System`). Every affected site already receives `RevalQueryOptions` or is built by something that does: `TtlQueryGarbageCollector(RevalQueryOptions)`; `RevalClient(…, RevalQueryOptions)`, which builds workers and states; `QueryWorker`, which builds `ExponentialBackoffRetryPolicy`; and `MutationState.cs:37`. Tests that construct `new RevalClient(sp, new RevalQueryOptions { TimeProvider = fake })` and DI users of `AddRevalQuery(o => …)` are then both covered.
- **Retry policy.** `ExponentialBackoffRetryPolicy` gets a constructor taking `TimeProvider` (default `System`). The public `IRetryPolicy` signature stays unchanged.
- **Persisted freshness.** `QueryFreshness` timestamps then compare against the same clock, which makes `ReviewFindingsTests.cs:113, 131, 133` exact.
- **ADR points.** Why `TimeProvider` (BCL on net8, no new dependency, documented test seam). Why synchronous arming (FakeTimeProvider cannot see timers armed later; dotnet/extensions#6404 and #7515). Why `CreateTimer` and not `PeriodicTimer` (dotnet/extensions#6810: one tick per wait, no multi-period advance).
- **What it does not cover.** Bodies of `Task.Run` (`RevalClient.cs:120` persistence load) and async handler continuations on the thread pool. Those stay in the gate and signal world of part A.

### C. Test framework (optional, after A, independent of B)

9. **Move both projects to xUnit v3** if B is adopted.
   - The main reason is the removal of `AsyncTestSyncContext`, which the `FakeTimeProvider` README names as the cause of hangs under v2. Until then, tests that `Advance` must call `SynchronizationContext.SetSynchronizationContext(null)` first.
   - Secondary reasons: `TestContext.Current.CancellationToken` to pass into gates and hang guards, `Timeout` on sync tests, and `Explicit` for the stress tests.
   - Cost: package rename, `OutputType Exe`, `ValueTask` lifetimes, and a check of bUnit compatibility (unverified).
   - Do **not** rely on `-parallel none` or `DisableTestParallelization` as the fix (topic 4).
10. **bUnit 2.x** is an API migration with no determinism gain (same timer model, 1 s default). Do it on its own merits.

### Pattern-to-technique map

| Pattern | Technique | Needs library change? |
|---|---|---|
| P1a sleep where the work is synchronous | Assert right after the call | No |
| P1 sleep for async work (persistence, async handler) | Gates, then await `RestoreCompleted`/fetch `Task`/`OnChanged` | No |
| P2 negative after sleep | Synchronous assert / gate + signal / fake-time advance / documented limitation | No (B for time-driven ones) |
| P3 interleaving by timing | Gates at both ends, released in scenario order | No |
| P4 polling, backoff, GC, staleness | `FakeTimeProvider.Advance` | **Yes (B, ADR)** |
| P5 silent / racy waits | Loud, subscribe-then-check, `RunContinuationsAsynchronously` | No |
| P6 bUnit on non-render state | Wait on `OnChanged`, not on renders | No |
| P7 shared statics, clock identity | Per-test locals, counters | No |
| P8 hang guards | Keep as guards or move to v3 test `Timeout` (open question 3) | No |
| P9 vacuous tests | Rewrite under fake time | B |

## Open questions for the maintainer

1. **Synchronous arming.** Is it acceptable that the poll and GC timers become `ITimer`s armed synchronously, with callbacks on the timer thread (production) or on the `Advance` caller (tests)? The callbacks would call `RunIfAllowed`/`CollectExpiredEntries` directly instead of from a `Task.Run` loop. This is a threading change worth its own paragraph in the ADR, given the thread-safety ADR (`2026-09-28-supported-hosting-models-and-the-thread-safety-contract.md`).
2. **Negative outcomes that stay asynchronous.** For those (e.g. "a late restore never overwrites" if the overwrite decision happens on the `Task.Run` thread), would you rather have a library quiescence signal (an internal or test-only "worker idle" `Task`), or a documented limitation per test?
3. **Hang guards.** Do `WaitAsync(TimeSpan.FromSeconds(5))` guards (P8) count as forbidden "timers"? They never decide pass or fail, they only convert a hang into a failure. The v3 alternative is a test-level `Timeout` plus `TestContext.Current.CancellationToken`.
4. **GC model.** Per-key GC timers (TanStack `removable.ts`) instead of the periodic sweep: in scope for the same ADR, or separate?
5. **Exposing the clock.** Should `TimeProvider` be public on `RevalQueryOptions`, or internal behind `InternalsVisibleTo` for tests only? Public lets apps fake time too, as TanStack's `timeoutManager` lets apps swap timers. Internal keeps the API surface unchanged.

## Sources

**Microsoft / .NET**
- TimeProvider overview: https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview
- Task.Delay (net8 overloads): https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-8.0
- PeriodicTimer constructors: https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer.-ctor?view=net-8.0
- TaskCreationOptions: https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcreationoptions?view=net-8.0
- Unit testing best practices: https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices
- dotnet/runtime Task.cs (release/8.0): https://github.com/dotnet/runtime/blob/123e7aa4d4ada32bf0ac2ea0f20dcc0710f70d05/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs#L5693-L5790
- dotnet/runtime#36617 (TimeProvider proposal): https://github.com/dotnet/runtime/issues/36617

**dotnet/extensions (FakeTimeProvider)**
- FakeTimeProvider.cs: https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider.cs
- README: https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/README.md
- csproj: https://github.com/dotnet/extensions/blob/3a3b4c680a5f5f11b6a123d1c473352aef826be9/src/Libraries/Microsoft.Extensions.TimeProvider.Testing/Microsoft.Extensions.TimeProvider.Testing.csproj
- Issues: #5326 https://github.com/dotnet/extensions/issues/5326, #5722 https://github.com/dotnet/extensions/issues/5722, #6810 https://github.com/dotnet/extensions/issues/6810, #6680 https://github.com/dotnet/extensions/issues/6680, #4912 https://github.com/dotnet/extensions/issues/4912, #6404 https://github.com/dotnet/extensions/issues/6404, #7515 https://github.com/dotnet/extensions/issues/7515, #5765 https://github.com/dotnet/extensions/issues/5765
- NuGet nuspec 10.11.0: https://api.nuget.org/v3-flatcontainer/microsoft.extensions.timeprovider.testing/10.11.0/microsoft.extensions.timeprovider.testing.nuspec

**bUnit**
- v1.40.0 WaitForHelper.cs: https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/WaitForHelper.cs
- v1.40.0 TestContextBase.cs: https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/TestContextBase.cs
- v1.40.0 RenderedFragmentWaitForHelperExtensions.cs: https://github.com/bUnit-dev/bUnit/blob/v1.40.0/src/bunit.core/Extensions/WaitForHelpers/RenderedFragmentWaitForHelperExtensions.cs
- Docs: https://bunit.dev/docs/interaction/awaiting-async-state.html, https://bunit.dev/docs/verification/async-assertion.html
- v2.9.0 sources and 1→2 migration: https://github.com/bUnit-dev/bUnit/blob/v2.9.0/docs/site/docs/migrations/1to2.md, https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/Extensions/WaitForHelpers/WaitForHelper.cs, https://github.com/bUnit-dev/bUnit/blob/v2.9.0/src/bunit/BunitContext.cs

**xUnit**
- Running tests in parallel: https://xunit.net/docs/running-tests-in-parallel
- v3 what's new: https://xunit.net/docs/getting-started/v3/whats-new
- v3 migration: https://xunit.net/docs/getting-started/v3/migration
- v2 FactAttribute.cs: https://github.com/xunit/xunit/blob/055b428274f62130f0d91d3c7e8aab7fb8699b0a/src/xunit.core/FactAttribute.cs
- v3 FactAttributeBase_aot.cs: https://github.com/xunit/xunit/blob/e91cdbe7557649b1179fbc47b01e8d3a615d1256/src/xunit.v3.core.aot/Internal/FactAttributeBase_aot.cs
- xunit.v3 4.0.2 nuspec: https://api.nuget.org/v3-flatcontainer/xunit.v3/4.0.2/xunit.v3.nuspec

**Coyote**
- Microsoft.Coyote.Test 1.7.11 nuspec: https://api.nuget.org/v3-flatcontainer/microsoft.coyote.test/1.7.11/microsoft.coyote.test.nuspec
- microsoft/coyote#521: https://github.com/microsoft/coyote/issues/521
- microsoft/coyote#506: https://github.com/microsoft/coyote/issues/506

**TanStack Query (commit ee47993)**
- queryObserver.test.tsx: https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/queryObserver.test.tsx
- query.test.tsx: https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/query.test.tsx
- retryer.test.tsx: https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src/__tests__/retryer.test.tsx
- sleep.ts: https://github.com/TanStack/query/blob/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-test-utils/src/sleep.ts
- timeoutManager.ts, removable.ts, queryObserver.ts, utils.ts: https://github.com/TanStack/query/tree/ee47993724e5b71e65d9f38ae416367d2dd18395/packages/query-core/src
- PRs: #11676 https://github.com/TanStack/query/pull/11676, #11744 https://github.com/TanStack/query/pull/11744, #11657 https://github.com/TanStack/query/pull/11657, #11460 https://github.com/TanStack/query/pull/11460
- Vitest `vi` API: https://vitest.dev/api/vi.html
