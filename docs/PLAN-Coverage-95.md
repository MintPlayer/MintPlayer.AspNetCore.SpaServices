# Plan: Raising coverage from 81.4% to 95%

Implementation plan for [PRD-Coverage-95.md](./PRD-Coverage-95.md).

**Single PR.** It includes every bug fix (B1–B16), every dead-code removal, and the gate change.

Baseline (`c64b014`, Release, net10 and net11 merged): **1645/2021 = 81.4%**, 518 tests.
Measured on Linux CI only. The 17 Windows Job Object lines in `ProcessTracker` stay uncovered.

## The arithmetic

These unlock figures are **estimates**, taken from reading the uncovered regions (four investigators,
one per area). The running percentages are indicative, not promises. New lines from seams and fixes
are added to the denominator as they arrive.

| M | Change | Δ covered | Δ valid | Covered/Valid | % |
|---|---|---|---|---|---|
| — | baseline | — | — | 1645/2021 | **81.4%** |
| M1 | Cheap wins: Prerendering, Proxying, Routing, StaticFiles, Utils | +34 | 0 | 1679/2021 | 83.1% |
| M2 | Delete dead or duplicated code | 0 | −8 | 1679/2013 | 83.4% |
| M3 | AngularCli (+B11–B13) | +30 | +2 | 1709/2015 | 84.8% |
| M4 | Npm real-process adapters (+B7–B10) | +40 | +7 | 1749/2022 | 86.5% |
| M5 | Prerenderer launcher seam, `PerformProxyRequest` connector (+B14–B16) | +32 | +4 | 1781/2026 | 87.9% |
| M6 | NodeServices without a seam (+B2) | +62 | +2 | 1843/2028 | 90.9% |
| M7 | S2 extraction and draining delay (+B3–B5) | +55 | +8 | 1898/2036 | 93.2% ← short of 95 |
| M8 | S1 `INodeProcess` seam (+B1, B6) | +118 | +30 | 2016/2066 | **~97.6%** ← clears 95 |
| M9 | Gate and doc corrections | 0 | 0 | — | — |

About 50 lines remain uncovered at the end: 17 Job Object lines, about 12 `SystemNodeProcess` adapter
lines, and about 20 scattered branches. Even if M8 delivers only 60% of its estimate, the total is
still 95.4%. **M8 is on the critical path. Without it the target cannot be reached.**

## Milestones

Per the batching rule, tests run once, at the end (Verification). Check each milestone with
`dotnet build -c Release` plus a read-through. A commit per milestone is fine.

### M1: Cheap wins, no seams

New tests. There are no production changes except where noted.

- `SpaPrerenderingExtensions`: `Excluded_path_skips_prerendering` covers the `ExcludeUrls` lambda and
  its short-circuit (:80, :101-104). An identity theory with a multi-value `Content-Encoding` covers :604.
- `SpaRouteService`: `GenerateUrl<T>(name, anon, HttpContext)` (:177-179).
- `ConditionalProxyMiddleware`: a prefix `"api"` gets its leading `/` added. `/api/x` is proxied and
  `/apix` is not (:55).
- `ClientWebSocketConnector`: target `ws://127.0.0.1:9/`, a bad header name and a pre-cancelled
  token. The swallowed `ArgumentException` covers :44 and the dispose-and-rethrow covers :66-69. If a
  connect attempt is ever observed, fall back to a non-`ws` scheme.
- `SpaProxy.PerformProxyRequest`: websocket branch through the real connector with a pre-cancelled
  `RequestAborted` (:86).
- `SpaProxy.PumpWebSocket`: `ReceiveAsync` returns a pending task, which covers the poll loop (:280, :292).
- `UseProxyToSpaDevelopmentServer`: invoke the pipeline twice with a pre-cancelled request and assert
  the factory ran twice (:71-73).
- `UseSpaStaticFilesImproved()`, the parameterless overload (:52).
- `EventedStreamReader`: 1200 lines. The newest line matches. A line trimmed from the 1000-line
  history throws `EndOfStreamException` and is not replayed (:225-227).

### M2: Delete dead or duplicated code

- `AngularCliMiddleware.cs:20-23`: the unreachable `SourcePath` guard.
- `AngularCliMiddleware.cs:26-27`: the default regex, duplicated at :89-92. Keep one copy.
- `SpaPrerenderingExtensions.cs:552-553, 620`: the `TryGetBuffer == false` fallbacks. Use
  `GetBuffer()` / `Length` on the known `MemoryStream`.
- `SpaProxyingExtensions.cs:71`: unused `didProxyRequest`.

### M3: AngularCli

- Move the private `ScriptedLauncher` (`AngularCliMiddlewareTests.cs:165`) into `TestHelpers`, because
  M5 reuses it.
- `Attach_registers_a_proxy_whose_requests_surface_the_startup_failure`: set
  `PackageManagerCommand = "no-such-pm-" + Guid`. `Attach` returns, and the first request surfaces
  `InvalidOperationException`. On Linux the launch throws. On Windows `cmd /c` prints "not recognized".
  Both paths end up in the returned task.
- `UseAngularCliServer_with_a_source_path_attaches_the_middleware`, with and without
  `SpaOptions.CliRegexes`.
- `Retries_the_readiness_probe_after_a_failed_attempt`: the handler throws, then returns 404, so the
  call count is 2. It pays the 500 ms production back-off once.
- `Logs_the_port_it_starts_on`: a capturing logger at Information.
- **B11**: check `m.Groups["openbrowser"].Success`. Test: an optional group that does not match leaves
  the earlier URL intact.
- **B12**: keep the caller's `MatchTimeout` when rebuilding the regex. Test: a regex with a custom
  timeout keeps it.
- **B13**: observe the fire-and-forget start task and log its fault. Test: a startup failure is logged
  without any request arriving.

### M4: Npm

- `SystemProcessLauncher_starts_kills_and_disposes_a_real_process`: `cmd.exe` on Windows, otherwise
  `cat`, with stdin blocked. Poll `HasExited` with a deadline of 10 s or less.
- `Public_ctor_launches_the_package_manager_for_real`: `pkgManagerCommand = "echo"`, then
  `WaitForMatch("run build --")`.
- `Writes_newline_free_stderr_chunks_to_the_console`: `FakeLauncher`, `Console.SetOut`, in the
  non-parallel collection.
- `ProcessTracker.KillTrackedProcesses`: widen `private` → `internal`. The test adds one live process
  and one disposed process whose `HasExited` throws, then asserts the live one dies. Non-parallel.
- **B7**: dispose `_npmProcess`, and make `Dispose` idempotent and thread-safe
  (`Interlocked.Exchange`). Test: a double concurrent dispose kills once and does not throw.
- **B8**: remove the process from `ProcessTracker` on `Exited`, and log a failed
  `AssignProcessToJobObject`. Test: the tracked count drops after exit.
- **B9**: write a partial stderr chunk once, not twice. Test: a line split across two reads appears
  once on the console.
- **B10**: fix the message typos and drop the embedded `PATH` from the message. Test: assert the
  message text.

### M5: Prerenderer launcher and proxy connector (make PLAN-Coverage-Raise M6/M7 true)

- `AngularPrerendererBuilder`: add `internal IProcessLauncher ProcessLauncher { get; init; } =
  SystemProcessLauncher.Instance;` and route it to the internal runner ctor.
  Tests: `Build_completes_after_two_Build_at_lines` (asserts `--watch` and the script name), and
  `Build_fails_with_output_when_script_exits_without_success`.
- **B14**: dispose the runner when `Build` fails or times out. Test: the fake child is killed on failure.
- `SpaProxy`: add an internal `PerformProxyRequest` overload that takes `IWebSocketConnector?`. Test:
  a fake connector and an immediately closed socket give `true` and destination `ws://…/path?q` (:87).
- **B15**: propagate `AcceptProxyWebSocketRequest`'s result. Test: a `WebSocketException` gives a 400
  and `false`.
- **B16**: make cancellation during the pump delay return quietly, the same as at the check. Test: a
  pump cancelled mid-delay completes without an exception.

### M6: NodeServices without a seam

- `OutOfProcessNodeInstance`: a test subclass with `nodePath: "no-such-node-" + Guid` gets
  `InvalidOperationException("Failed to start Node process")`, with and without `launchWithDebugging`.
  A finalizer test in a `[NoInlining]` helper, followed by `GC.Collect()`, covers the null-process
  `Dispose(false)` path.
- `HttpNodeInstance`: construction with a bogus `NodePath` covers the static init and the base
  arguments (:22-47, :55).
- `NodeServicesOptionsExtensions.UseHttpHosting`: the factory builds an `HttpNodeInstance` (:19-20).
- `StringAsTempFile`: the finalizer deletes the file (:79-80). Non-parallel.
- **B2**: dispose the entry-point temp file when the launch throws. Test: after the failed ctor above,
  the file is gone.

### M7: S2 extraction and draining delay

- `HttpNodeInstance`: extract `internal static string? ParseEndpoint(string line)` (:115-123) and
  `internal static Task<T> ReadResponseAsync<T>(HttpResponseMessage, CancellationToken)` (:65-105).
  `ParseEndpoint` theories: IPv4, `::1`, other IPv6, and a line that does not match.
  `ReadResponseAsync` cases: text to string, text to int (throws), json, octet-stream to `Stream` and
  to `object`, octet-stream to string (throws), unknown media type (throws), and a 500 with a JSON
  error body giving `NodeInvocationException`.
- **B3**: a missing `Content-Type` gives a clear `InvalidOperationException`, not an NRE.
- **B4**: an error response with an empty or non-JSON body gives `NodeInvocationException` carrying
  the status code and the raw body.
- **B5**: bracket every IPv6 address, not only `::1`.
- `NodeServicesImpl`: add an internal ctor parameter `TimeSpan drainingDelay` (default 15 s). Test:
  `Delayed_dispose_exception_is_rethrown_on_next_call` with a 10 ms delay (:114-122).

### M8: S1 `INodeProcess` seam (critical path)

- Add internal `INodeProcess` with `HasExited`, `Kill()`, `Exited` and
  `BeginReadLines(Action<string?> stdout, Action<string?> stderr)`, plus `SystemNodeProcess`, a thin
  adapter over `Process` and `OutputDataReceived`.
- Add an internal `OutOfProcessNodeInstance` ctor that takes `Func<ProcessStartInfo, INodeProcess>`.
  The public ctor chains to it with `SystemNodeProcess`, so its shape does not change.
- Add an internal `HttpNodeInstance` ctor that takes an `HttpMessageHandler`.
- `FakeNodeProcess` in `TestHelpers`, with `EmitStdout` / `EmitStderr` / `Exit`.
- Tests:
  - `Invoke_waits_for_Listening_then_returns`
  - `Stdout_and_stderr_are_logged_unencoded`
  - `Debugger_noise_on_stderr_logs_as_warning`
  - `HasExited_throws_unavailable`, with debugging on and off (`AllowConnectionDraining`)
  - `Never_listening_times_out_connecting` (50 ms timeout)
  - `Hung_invocation_times_out`
  - `Caller_cancel_rethrows_TaskCanceled`
  - `Dispose_kills_a_live_process_and_the_watcher`
  - `Watched_file_change_forces_restart` and `Rename_forces_restart`: a real `FileSystemWatcher` on a
    dedicated temp dir, with a bounded wait of 5 s or less
  - `HttpNodeInstance`: `Invoke_posts_camelCase_payload_to_parsed_endpoint`,
    `Second_port_line_is_logged`, `Dispose_disposes_client_once`
- **B1**: fault `_connectionIsReadySource` when the process exits before it is listening. Test: exit
  before "Listening" makes callers fail fast with "Node process exited before it was ready", not a
  60 s timeout.
- **B6**:
  - dispose non-stream `HttpResponseMessage`s
  - dispose `_nodeProcess`
  - use `Kill(entireProcessTree: true)`
  - create the TCS with `RunContinuationsAsynchronously`

  Test: after `Dispose`, the fake records the tree kill and its own disposal.

### M9: Gate and documentation

- `coverage.yml`: `projectTarget: 95`, `patchTarget: 90`. Update the comment block with the baseline
  (81.4%) and a pointer to this plan. Because the file is read from the base ref, the new gate first
  applies to the PR after this one.
- `PLAN-Coverage-Raise.md` M6/M7: correct them to say what actually shipped, and point to this plan.
- Mark the milestones ✅ here, and record the measured outcome next to the estimates.

## Verification

Run once, after M9:

```
dotnet build --configuration Release > build.log 2>&1
dotnet test --no-restore --no-build --configuration Release --settings coverlet.runsettings \
  --collect:"XPlat Code Coverage" --results-directory coverage > test.log 2>&1
```

- All tests pass on net10.0 and net11.0. The 518 existing tests are unchanged.
- Merged line coverage is ≥ 95% locally. Confirm it on the PR's Linux CI upload, which is the number
  that counts. Windows local runs read slightly higher because of the Job Object lines.
- Wall-clock for the suite stays well under a minute (NFR-1.2).
- Run the suite three times in a row to check that the real-process, `FileSystemWatcher` and
  finalizer tests are not flaky.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| M8 underdelivers | Medium | M1–M7 reach ~93%. M8 needs to deliver only ~45% of its estimate. |
| S1 changes real-process behaviour | Low | The adapter is a verbatim move of today's event wiring. The public ctor is unchanged. |
| Real-process or watcher tests are flaky on CI | Medium | OS built-ins only, bounded waits, dedicated temp dirs, non-parallel collection. |
| A B-fix changes behaviour someone relies on | Low | Every B-fix turns a crash, hang or leak into a defined outcome. None changes a success path. |
