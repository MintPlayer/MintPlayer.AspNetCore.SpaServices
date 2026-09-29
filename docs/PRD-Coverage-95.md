# PRD: Raising coverage from 81.4% to 95%

## Overview

Take line coverage over the shipped libraries from **81.4%** to at least **95%**, and raise the gate to
match.

This is the successor to [PRD-Coverage-Raise.md](./PRD-Coverage-Raise.md) (64.4% → 80%). Its
decisions and non-functional requirements stay binding unless amended explicitly below.

## Problem Statement

Baseline, measured locally on `c64b014` (Release, both TFMs merged, 518 tests):
**1645/2021 = 81.4%**. 376 lines are uncovered. Reaching 95% leaves room for about 101.

| Area | Coverage | Uncovered |
|---|---|---|
| `NodeServices/HostingModels` | 20.5% | **221** |
| `SpaServices/Npm` | 63.0% | 50 |
| `SpaServices/AngularCli` | 57.7% | 33 |
| `SpaServices.Prerendering` (root + `Prerendering/`) | 57.1% / 98.5% | 27 + 8 |
| `SpaServices/Proxying` | 89.1% | 23 |
| `NodeServices` root + `Util` | 91.4% / 95.9% | 5 + 2 |
| `Routing/Services`, `StaticFiles`, `Utils` | 97–98% | 3 + 1 + 3 |
| Everything else | 100% | 0 |

The gap is concentrated. `OutOfProcessNodeInstance` and `HttpNodeInstance` alone account for 219
lines. The previous PRD scoped that restructuring out on purpose ("80% is reachable without it"). It
is not optional at 95%.

Everything outside HostingModels is uncovered for one of three reasons:

1. **Real-implementation adapters behind existing seams were never exercised.** Examples are
   `SystemProcessLauncher`, `ClientWebSocketConnector` and `ProcessTracker`. The fakes cover the
   callers. The adapters themselves need a real (OS built-in) process or a pre-cancelled connect.
2. **Wiring entry points were never called.** Examples are `AngularCliMiddleware.Attach`,
   `UseSpaStaticFilesImproved()`, the `UseProxyToSpaDevelopmentServer` terminal delegate and the
   `ExcludeUrls` branch.
3. **Two seams the previous plan claimed to have threaded through were not.**
   `AngularPrerendererBuilder.Build` (`AngularPrerendererBuilder.cs:54`) still calls the public,
   real-process `NodeScriptRunner` constructor, although PLAN-Coverage-Raise M7 says otherwise. The
   `IWebSocketConnector` reaches `AcceptProxyWebSocketRequest` but not `PerformProxyRequest`, although
   M6 says it does.

### Bugs found during the investigation

These were found while reading the uncovered code. As in the previous two coverage PRDs, they are
fixed here, each with a test, and not left in shipped code.

| # | Where | Defect |
|---|---|---|
| B1 | `OutOfProcessNodeInstance.cs:26` | `_connectionIsReadySource` is never faulted when the process exits. If node crashes at startup, every caller waits out the full invocation timeout (60 s) and then gets a misleading "Attempt to connect to Node timed out". With a timeout ≤ 0 they hang forever. |
| B2 | `OutOfProcessNodeInstance.cs:66-72` | If the launch throws, the entry-point `StringAsTempFile` is never disposed and stays registered on the stopping token. |
| B3 | `HttpNodeInstance.cs:74-75` | A response with no `Content-Type` throws `NullReferenceException`. |
| B4 | `HttpNodeInstance.cs:68-71` | An error response with an empty or non-JSON body (for example an HTML 502) throws `NullReferenceException` or `JsonReaderException` instead of `NodeInvocationException`. |
| B5 | `HttpNodeInstance.cs:122` | Only `::1` gets brackets. Any other IPv6 listen address produces an invalid URI. |
| B6 | `HttpNodeInstance.cs:63`, `OutOfProcessNodeInstance.cs:322` | Non-stream `HttpResponseMessage`s and `_nodeProcess` are never disposed. `Kill()` does not kill the process tree. The ready TCS is created without `RunContinuationsAsynchronously`, so waiters resume on node's stdout reader thread. |
| B7 | `NodeScriptRunner.cs:175-182` | `Dispose` never disposes the `Process`. It also races when both the stopping-token callback and an explicit dispose run, because `HasExited` and `Kill` are not synchronized. |
| B8 | `ProcessTracker.cs:114-125` | Processes are added but never removed. The static list grows for the life of the app. The `AssignProcessToJobObject` result is also ignored. |
| B9 | `NodeScriptRunner.cs:145-149` | A stderr line split across two reads is written to the console twice: once as a partial chunk, then again as the full line. |
| B10 | `NodeScriptRunner.cs:166-168` | The error message has typos ("To resolve this:.", "enviroment") and embeds the full `PATH` in the logs. |
| B11 | `AngularCliMiddleware.cs:97` | `Groups.ContainsKey("openbrowser")` is true for any declared group, even one that did not match. An unmatched optional group then writes `""` over a good URL, and `new Uri("")` throws `UriFormatException`. The check should be `Groups["openbrowser"].Success`. |
| B12 | `AngularCliMiddleware.cs:96` | Rebuilding the caller's regex swaps its `MatchTimeout` for a hard-coded 5 s. |
| B13 | `AngularCliMiddleware.cs:40-45` | `Attach` fires the dev-server start and never observes it. If no request arrives, a startup failure is never logged. |
| B14 | `AngularPrerendererBuilder.cs:54` | `Build` never disposes its `NodeScriptRunner`, so after a failure or timeout the `--watch` npm process keeps running until the host stops. |
| B15 | `SpaProxy.cs:86-87` | `AcceptProxyWebSocketRequest`'s `false` (returned after a 400) is ignored, so the caller always sees "proxied". |
| B16 | `SpaProxy.cs:282,292` | Cancellation in `PumpWebSocket` behaves two ways. It returns quietly at the check, but throws `TaskCanceledException` from the delay. |

Dead or duplicated code found at the same time: the `SourcePath` guard at
`AngularCliMiddleware.cs:20-23` cannot be reached, because the only caller rejects an empty path first
(`AngularCliMiddlewareExtensions.cs:218-221`). The default regex is duplicated at :26-27 and :89-92.
Both `TryGetBuffer == false` fallbacks in `SpaPrerenderingExtensions.cs:552-553,620` cannot be
reached, because the only buffer is `new MemoryStream()`. The `didProxyRequest` variable in
`SpaProxyingExtensions.cs:71` is unused.

### What is *not* a problem

- **Genuinely untestable code is small.** The only platform-bound block is the Windows Job Object
  branch in `ProcessTracker.cs:24-45,56` (17 lines). CI measures on `ubuntu-latest`, so those lines
  stay uncovered there. That is about 0.8 points, and it is accepted rather than excluded (NFR-3.1).
- **No dead code in NodeServices or Npm.** Everything uncovered there is reachable.

## Proposed Solution

### Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Restructure `OutOfProcessNodeInstance` | **Yes**: internal `INodeProcess` seam (S1) | Previously out of scope. It is ~114 lines, and 95% is unreachable without it. |
| Spawn real node in tests | **No**: NFR-1.1 stands | No `setup-node` in CI. xUnit 2.9 has no dynamic skip, and a silent skip would lose coverage invisibly. |
| Reuse Npm's `IProcessLauncher` for NodeServices | **No**: a new internal `INodeProcess` | SpaServices references NodeServices, not the reverse. `IProcessLauncher` exposes `StreamReader`s. `OutOfProcessNodeInstance` consumes `OutputDataReceived` events, and `DataReceivedEventArgs` has no public constructor. |
| Real processes for adapter tests | **Yes, OS built-ins only** | `cmd.exe` / `cat` (stdin-blocked, killable) and `echo`. These are not node or npm, so NFR-1.1 holds. |
| Pure-logic extraction in `HttpNodeInstance` (S2) | `internal static ParseEndpoint`, `ReadResponseAsync<T>` | The same precedent as `BuildNodeProcessStartInfo` / `IsDebuggerMessage`. |
| `NodeServicesImpl` draining delay | Internal constructor parameter, default 15 s | Otherwise the delayed-dispose path finishes only after the test has ended. |
| `ProcessTracker.KillTrackedProcesses` | Widen `private` → `internal` | It runs only on `ProcessExit`, which is where coverlet flushes, so it is never counted. |
| `AngularPrerendererBuilder` | Internal `ProcessLauncher` init property, default `SystemProcessLauncher.Instance` | This is what PLAN-Coverage-Raise M7 claimed. Make it true. |
| `SpaProxy.PerformProxyRequest` | Internal overload taking `IWebSocketConnector?` | This is what M6 claimed. Make it true. |
| Dead code (see above) | **Delete** | This is a genuine simplification, stated explicitly (NFR-3.2), and it shrinks the denominator by about 6 lines. |
| Windows Job Object lines | Accept the gap | 17 lines. Excluding them breaks NFR-3.1. Adding a Windows coverage job costs a CI round on every PR. |
| Gate | `projectTarget: 95`, `patchTarget: 90` | 95 is the stated goal. The plan lands at about 96–97% on Linux, so there is a margin of 1–2 points. |

### The denominator moves, and that must be said out loud

About −6 lines from deleting dead code. About +30 lines from new seams, bug fixes and extractions.
About 12 of those (the `SystemNodeProcess` adapter body over `Process` events) stay uncovered by
design, because covering them would require real node. Net: the denominator goes up, not down.
The number is not bought by shrinking it.

## Requirements

### Functional

- **FR-1** Line coverage over the shipped libraries SHALL be at least **95%**, as measured by the
  Linux CI run that uploads to coverage.mintplayer.com.
- **FR-2** `coverage.yml` SHALL set `projectTarget: 95` and `patchTarget: 90`. All other fields are
  unchanged.
- **FR-3** New seams (S1 `INodeProcess`, the `HttpMessageHandler` constructor, the draining delay, the
  prerenderer `ProcessLauncher`, the `PerformProxyRequest` connector overload) SHALL be `internal` and
  SHALL default to today's implementation. No public API change.
- **FR-4** Each of B1–B16 SHALL be fixed, and each fix SHALL have a test that fails without it.
- **FR-5** The dead or duplicated code listed above SHALL be removed.
- **FR-6** PLAN-Coverage-Raise.md M6 and M7 SHALL be corrected to describe what actually shipped, with
  a pointer to this PRD.

### Non-functional: inherited, still binding

- **NFR-1.1** No test may require node, npm, network, a database or a real web server. OS built-ins
  (`cmd`, `cat`, `echo`) are allowed. So are loopback connects that are cancelled before they dial.
- **NFR-1.2** No test may sleep for a fixed delay. Waits are bounded polls with a deadline. Two tests
  pay a production back-off once (500 ms Angular CLI retry, 100 ms websocket pump). That is accepted,
  and it is not a test-side sleep.
- **NFR-3.1** No assembly exclusion list, no `ExcludeByAttribute`, no new `[ExcludeFromCodeCoverage]`.
- **NFR-3.2** Coverage is not raised by shrinking the denominator, except where the shrink is itself a
  genuine improvement (the dead code above).
- **NFR-4.1** Apart from B1–B16, there is no production behaviour change. All 518 existing tests SHALL
  pass unchanged.
- **NFR-5** *(new)* Tests that touch process-global state (`Console.SetOut`, `KillTrackedProcesses`,
  finalizers via `GC.Collect`) SHALL live in a `[Collection(DisableParallelization = true)]`
  collection.
- **NFR-6** *(new)* Tests that depend on timing (`FileSystemWatcher`, real-process exit) SHALL use a
  dedicated temp directory and a bounded wait of ≤ 10 s, so that a hang fails the test instead of
  hanging the host.

## Out of scope

These are judged not worth doing. They are not deferred work.

- **A Windows coverage leg in CI**, to count the 17 Job Object lines. It adds a full CI round to every
  PR for 0.8 points.
- **A real-node smoke test** of the `entrypoint-http.js` ↔ C# contract. It would need `setup-node` and
  a reversal of NFR-1.1. The contract is a single regex (`HttpNodeInstance.cs:23`), which S2 unit-tests
  directly.
- **Quoting `scriptName`/`arguments` in `NodeScriptRunner`** (`:49,56`). The input comes from the
  developer. Changing how the command line is built is a behaviour change with no reported defect.
- **The fake-`ng serve`-script happy path for AngularCli.** It adds nothing to AngularCli coverage.
  The pre-cancelled / bogus-command route covers every line.

## Risks

| Risk | Mitigation |
|---|---|
| S1 refactors a public abstract class | The constructor shape is unchanged. The public constructor chains to the internal one with the real adapter. The existing tests are the regression net. |
| Real-process tests are flaky on CI | Only `cat`/`echo`/`cmd`. Bounded `HasExited` polling (NFR-6). |
| `ClientWebSocket` dials before it checks a pre-cancelled token | If CI shows a connect attempt, switch to a non-`ws` scheme. That throws `ArgumentException` synchronously and hits the same dispose-and-rethrow block. |
| Finalizer tests depend on the GC | `[MethodImpl(NoInlining)]` helper plus `GC.Collect()` / `WaitForPendingFinalizers()`. Measured in Release (CI), and non-parallel (NFR-5). |
| Estimates are read from uncovered regions, not measured | The PLAN's arithmetic is indicative. There is a margin of 1–2 points over 95. |
