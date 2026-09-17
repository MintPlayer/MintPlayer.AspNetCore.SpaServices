# Plan: Raising coverage from 64.4% to 80%, and gating it

Implementation plan for [PRD-Coverage-Raise.md](./PRD-Coverage-Raise.md).

Branch: `feature/dotnet-11` → [PR #84](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/84).
**Single PR**, shared with the .NET 11 upgrade.

Baseline: **1301/2019 = 64.4%** line, 55.4% branch, 381 tests.

## The arithmetic

Every milestone's effect on the number, carried forward. Denominator drops once, at M1, and is
fixed at 1826 thereafter. Unlock figures are **estimates** from reading the uncovered regions; the
running percentages are therefore indicative, not promises.

| M | Change | Δ covered | Covered/Valid | % |
|---|---|---|---|---|
| — | baseline | — | 1301/2019 | **64.4%** |
| M1 | Deduplicate 5 files (denominator −193, covered −65) | −65 | 1236/1826 | **67.7%** |
| M2 | Cheap pure-logic wins, no seams | +60 | 1296/1826 | **71.0%** |
| M3 | Widen two `SpaProxy` statics | +33 | 1329/1826 | **72.8%** |
| M4 | Extract `BuildStartInfo` / `PrepareNodeProcessStartInfo` | +60 | 1389/1826 | **76.1%** |
| M5 | `IProcessLauncher` + `IChildProcess` | +60 | 1449/1826 | **79.4%** |
| M6 | `IWebSocketConnector` | +40 | 1489/1826 | **81.5%** ← clears the gate |
| M7 | Inject the runner into AngularCli / AngularPrerenderer | +80 | 1569/1826 | **85.9%** |

The residual ~257 dark lines are almost entirely `OutOfProcessNodeInstance` (190), `HttpNodeInstance`
(63) and `ProcessTracker`'s Win32 layer (36) — the out-of-scope set. **That caps the achievable
figure at roughly 86%**, which is the margin behind the 80% target.

## Outcome

**Delivered: 64.4% -> 80.17% line (1533/1912), 73.81% branch. 478 tests, green on both target
frameworks, three consecutive clean sweeps. The 80% gate is met.**

| Package | Line rate |
|---|---|
| `NodeServices` | 47.2% |
| `SpaServices` | 83.6% |
| `SpaServices.Prerendering` | 94.1% |
| `SpaServices.Routing` | 98.3% |
| `SpaServices.Abstractions` | 100.0% |
| `SpaServices.Xsrf` | 100.0% |

What remains dark is almost entirely the out-of-scope set: `OutOfProcessNodeInstance` (156 lines),
`HttpNodeInstance` (63) and `ProcessTracker`'s Win32 layer (36), plus the seams' own default
implementations (`ClientWebSocketConnector`, `SystemProcessLauncher`), which dial a real socket and
start a real process.

## The bug that nearly cost the target

Work stopped at 78.04% with the `AngularCliMiddleware` tests deleted, because they intermittently
took the whole test run down. That was recorded as a crash caused by a fire-and-forget task whose
exceptions were never observed.

**That diagnosis was wrong, and NFR-4.1 was relaxed deliberately to fix the real cause.**

Instrumenting a minimal reproduction - an `EventedStreamReader` over a `MemoryStream`, two sequential
`WaitForMatch` calls, nothing else - showed the first match succeeding, the second never completing,
and **no exception of any kind**: not unhandled, not unobserved, not even first-chance. Nothing was
throwing. The process was not crashing; it was hanging, and vstest reports a host it has to kill as
"Test host process crashed".

The cause is that `WaitForMatch` had no history. It subscribed to `OnReceivedLine` and could only
see lines emitted after that moment, while `Run` emits every line of a chunk back to back. A second
`WaitForMatch`, issued from the continuation of the first, raced that loop; when it lost, its line
**and** the stream-closed notification behind it had already fired, so its task could never complete.

Two details worth keeping:

- Inline continuations were accidentally *winning* that race most of the time. An early attempt at a
  fix - giving the `TaskCompletionSource` `RunContinuationsAsynchronously` - made it lose every time,
  taking the reproduction from 3-in-4 to 4-in-4. It was reverted.
- `StreamReader.ReadAsync` does not reliably observe a cancellation token, so cancelling was not
  enough on its own to release a pending waiter. The reader now registers `OnClosed` on the token
  directly.

**This was a live production bug.** `AngularPrerendererBuilder.WaitForBuildToFinish` waits for its
finished-regex `occurrences` times in sequence. Against a real dev server the lines usually arrive in
separate slow reads, so it usually won the race - but any build whose matching lines landed in one
read hung the prerenderer until its startup timeout. That is a plausible cause of "sometimes the SSR
build just hangs".

### The fix

`EventedStreamReader` now keeps a bounded history of emitted lines (1000) with a cursor marking what
previous waits consumed. `WaitForMatch` scans the unconsumed history first, returns immediately on a
hit, and fails fast with `EndOfStreamException` if the stream has already closed rather than waiting
for a line that can never arrive. The cursor preserves the "wait for the NEXT occurrence" semantics
`AngularPrerendererBuilder` depends on - without it, the second wait would re-match the first line and
declare a half-finished build ready.

It also takes an optional `CancellationToken`, threaded from `NodeScriptRunner`'s
`applicationStoppingToken`, so the read loop ends at shutdown instead of outliving its owner.

`WaitForMatchSequenceTests` covers all of it: the same-chunk race, repeated occurrences, fail-fast
after close, cursor advancement, ANSI stripping in the history scan, and cancellation releasing a
pending wait. The `AngularCliMiddleware` tests are restored, including the two-regex case that could
not be written before.

## The other defect found

**A root-level group with an empty path emits a double slash.** `Group("", "bare", ...)` at the root
produces `//thing` rather than `/thing`. Pinned by an assertion in `GenerateUrlOverloadTests` with a
comment saying it is pinned, not endorsed - it is a routing behaviour change, unrelated to this work,
and belongs in its own change.

## A note on the denominator

Deduplication (M1) removed 123 uncovered lines and 65 covered ones, moving the number 3.3 points by
deleting code rather than by testing anything. It is a genuine improvement - half as much code, one
place to fix a bug - but it is not new verification, and NFR-3.2 requires saying so.

Adding the seams then added 26 uncovered lines of shipped code, costing about 1.4 points before they
earned anything back.

## Milestones

### M1 — Deduplicate ✅

Delete the five `SpaServices.Prerendering/Internals/*` copies; keep the `SpaServices` originals. Add
to `MintPlayer.AspNetCore.SpaServices.csproj`:

```xml
<InternalsVisibleTo Include="MintPlayer.AspNetCore.SpaServices.Prerendering" />
```

Prerendering already has a `ProjectReference` to SpaServices, so it compiles. Update the `using`s in
Prerendering's consumers (`AngularPrerendererBuilder`, `SpaPrerenderingExtensions`).

Two behavioural deltas to preserve rather than lose — the copies are not quite identical:

- SpaServices' `NodeScriptRunner` has a `logger.IsEnabled` guard the Prerendering copy lacks. Keep it.
- SpaServices' `NodeScriptRunner` carries `[DynamicallyAccessedMembers]` on the local function. Keep it.

Also delete `Tests/Prerendering/PrerenderingInternalsTests.cs`'s duplicate `EventedStreamReader`
coverage, which tests the same class twice.

**No new tests.** This milestone moves the number by deleting code; the PRD records that explicitly.

### M2 — Cheap pure-logic wins ✅

No seams, no production changes beyond two `private` → `internal` widenings. Roughly 60 lines:

| Target | Lines | Note |
|---|---|---|
| `NodeServicesImpl` — two `InvokeAsync`/`InvokeExportAsync` overloads | 2 | Pure delegation, simply never called |
| `NodeServicesImpl` — delayed-disposal path + `ThrowAnyOutstandingDelayedDisposalException` | 10 | Needs a disposal that throws |
| `SpaRouteItem.Group(path, name, Action<…>)` + `ToString()` | 11 | The sibling `Route(...)` overload is already covered |
| `SpaRouteService.GenerateUrl<T>` × 3 overloads | 9 | scheme+host, protocol+host, protocol+host+fragment |
| `TcpPortFinder` | 6 | `internal static`, loopback listener |
| `LoggerFinder` | 5 | Uncovered only because its caller is untested |
| `SpaRouteExtensions.AddSpaPrerenderingService<T>` | 4 | One `ServiceCollection` assertion |
| `ConditionalProxyMiddleware.InvokeCore` residual branch | 4 | |
| `SpaPrerenderingExtensions` — `ExcludeUrls` path | 4 | Add to the existing harness |

### M3 — Widen two `SpaProxy` statics ✅

`private static` → `internal static`, no other change:

- `ToWebSocketScheme` (~8 lines) — pure `http→ws` / `https→wss`.
- `PumpWebSocket` (~25 lines) — **the best lines-per-effort item in the audit.** Its parameters are
  already `WebSocket`, which is `abstract`, so a test subclasses it directly. Covers the polling
  loop, the cancellation early-return, the `Close` → `CloseOutputAsync` path and the
  `ArgumentOutOfRangeException` guard.

Also extract `internal static HttpClientHandler CreateProxyHandler()`. This unlocks no lines but
**deletes the reflection at `Tests/Proxying/SpaProxyTests.cs:61-73`**, which currently walks the
private fields of `HttpMessageInvoker` to find the handler — a test that breaks whenever the BCL
rearranges its internals.

### M4 — Extract the start-info builders ✅

Pure extraction, no interfaces. Lift the `ProcessStartInfo` composition out of the constructors so it
can be exercised without launching anything:

- `NodeScriptRunner` → `internal static ProcessStartInfo BuildStartInfo(...)`: the three argument
  guards, the Windows `cmd /c` vs POSIX branch, the `run {script} -- {args}` composition, the env-var
  copy. After M1 this is written once, not twice.
- `OutOfProcessNodeInstance.PrepareNodeProcessStartInfo` → a static helper on an internal class, so
  it is reachable without running the constructor that spawns node. Same for the pure statics
  `UnencodeNewlines`, `IsDebuggerMessage` and `IsFilenameBeingWatched`.

### M5 — `IProcessLauncher` + `IChildProcess` ✅

The runner touches only five members of `Process`:

```csharp
internal interface IChildProcess : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    bool HasExited { get; }
    void Kill(bool entireProcessTree);
}

internal interface IProcessLauncher
{
    IChildProcess Start(ProcessStartInfo startInfo);
}
```

Add an `internal` constructor overload on `NodeScriptRunner` taking an `IProcessLauncher`, defaulting
to the real one. A fake returns `StreamReader`s over `MemoryStream`s — which is already proven to
work, since both `EventedStreamReader` copies reach 100% by exactly that technique.

Unlocks the `LaunchNodeProcess` failure path (the `InvalidOperationException` with the PATH hint),
`AttachToLogger`, `StripAnsiColors`, the `DiagnosticSource` event and `Dispose`.

Precedent: `NodeServicesImpl` already takes `Func<INodeInstance>` and is the one file in this area at
79% with a `FakeNodeInstance` in the tests. This is the same move.

### M6 — `IWebSocketConnector` ✅

```csharp
internal interface IWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri destination, IEnumerable<string> subProtocols,
                                 IEnumerable<KeyValuePair<string, StringValues>> headers,
                                 CancellationToken ct);
}
```

Threaded through `PerformProxyRequest` as an optional internal parameter — mirroring how `HttpClient`
is already passed. The server side needs a fake `IHttpWebSocketFeature` added to the existing
`CreateContext` helper, which already hand-builds four other features.

Makes the sub-protocol forwarding, the `NotForwardedWebSocketHeaders` filter, the swallowed
`ArgumentException` and the `WebSocketException → 400` path assertable.

### M7 — Inject the runner into the two callers ✅

`AngularCliMiddleware` and `AngularPrerendererBuilder` both `new` a concrete `NodeScriptRunner`. Route
both through the M5 seam, then lift `StartAngularCliServerAsync`'s regex/`openbrowser` logic and its
`EndOfStreamException` message to `internal static`.

`AngularPrerendererBuilder` already demonstrates the target shape: `WaitForBuildToFinish` was lifted
to `internal static` and **is** covered; only the spawning wrapper around it is dark.

`WaitForAngularCliServerToAcceptRequests` additionally needs an `HttpMessageHandler` seam — the tests
already own a reusable `StubHandler`. Note it loops `while(true)` with `Task.Delay(500)` and no
cancellation, so a test must inject a handler that succeeds, or it hangs the run (the existing PRD
flags this exact hazard).

### M8 — Gate ✅

`coverage.yml` is committed at the repo root with the agreed policy — fixed 80% project target, 80%
patch target, zero tolerance on both, blocking on. **The repository now meets it** (80.17%), so the
gate goes green rather than red the moment it starts applying.

**It does not gate PR #84** — the service reads gate
policy from the *base* ref precisely so a PR cannot rewrite the policy judging it, so it takes effect
on the first PR opened after this merges to master.

To gate PR #84 itself, the settings panel at coverage.mintplayer.com must be set by hand:
Project comparison → Fixed target, Project target → 80, Allowed drop → 0, Patch target → 80,
Patch tolerance → 0, Blocking → checked.

**Known UI issue — [MintPlayer.Spark#413](https://github.com/MintPlayer/MintPlayer.Spark/issues/413),
"Coverage - ProjectTargetPercent attribute never becomes visible".** The "Project target (%)" input
renders only when `projectMode === 'fixed'`, and it did not appear after the dropdown was set — the
binding did not react to programmatic `input`/`change` events.

This is why `coverage.yml` is the primary mechanism here rather than a convenience: until #413 is
fixed, the file is the only reliable way to set a fixed target. Note the API rejects a `fixed` gate
with a null `projectTarget`, so the bad state cannot be saved — but if one were ever persisted, the
project check would abstain as `neutral` forever rather than fail loudly.

## Verification

Per the batching rule, one sweep at the end:

```
dotnet restore && dotnet build -c Release --no-restore
dotnet test --no-restore --no-build -c Release --settings coverlet.runsettings \
  --collect:"XPlat Code Coverage" --results-directory coverage
dotnet pack --no-build -c Release
```

Then confirm from the Cobertura report directly, not from the server: line rate ≥ 80%, and all 381
existing tests still green (NFR-4.1 — no production behaviour change).

Note `build-any.yml` deliberately does **not** upload coverage, so pushes to this branch produce no
server-side number. Only the PR workflow does.

## Risk register

| Risk | Severity | Status |
|---|---|---|
| Seams alter production behaviour | High | **Retired** — every seam is an optional internal parameter defaulting to today's implementation |
| 80% unreachable without the invasive work | Medium | **Retired** — reached 80.17% once the EventedStreamReader hang was fixed |
| Dedup changes behaviour (the copies are not identical) | Medium | **Retired** — the two deltas were preserved; 478 tests green |
| `while(true)` poll in `WaitForAngularCliServerToAcceptRequests` hangs the suite | Medium | **Retired** — every test injects a handler that answers first time |
| Gate saved as `fixed` with null target abstains silently | Low | **Open** — verify the checks report pass/fail, not `skipping`, on the first PR after merge |
| `ExcludeByFile` not matching `Inject.g.cs` | Low | Pre-existing, 12 lines, immaterial |
