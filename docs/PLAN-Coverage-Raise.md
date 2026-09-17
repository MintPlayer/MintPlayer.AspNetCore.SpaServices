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

## Milestones

### M1 — Deduplicate ⬜

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

### M2 — Cheap pure-logic wins ⬜

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

### M3 — Widen two `SpaProxy` statics ⬜

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

### M4 — Extract the start-info builders ⬜

Pure extraction, no interfaces. Lift the `ProcessStartInfo` composition out of the constructors so it
can be exercised without launching anything:

- `NodeScriptRunner` → `internal static ProcessStartInfo BuildStartInfo(...)`: the three argument
  guards, the Windows `cmd /c` vs POSIX branch, the `run {script} -- {args}` composition, the env-var
  copy. After M1 this is written once, not twice.
- `OutOfProcessNodeInstance.PrepareNodeProcessStartInfo` → a static helper on an internal class, so
  it is reachable without running the constructor that spawns node. Same for the pure statics
  `UnencodeNewlines`, `IsDebuggerMessage` and `IsFilenameBeingWatched`.

### M5 — `IProcessLauncher` + `IChildProcess` ⬜

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

### M6 — `IWebSocketConnector` ⬜ *(crosses 80% here)*

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

### M7 — Inject the runner into the two callers ⬜

`AngularCliMiddleware` and `AngularPrerendererBuilder` both `new` a concrete `NodeScriptRunner`. Route
both through the M5 seam, then lift `StartAngularCliServerAsync`'s regex/`openbrowser` logic and its
`EndOfStreamException` message to `internal static`.

`AngularPrerendererBuilder` already demonstrates the target shape: `WaitForBuildToFinish` was lifted
to `internal static` and **is** covered; only the spawning wrapper around it is dark.

`WaitForAngularCliServerToAcceptRequests` additionally needs an `HttpMessageHandler` seam — the tests
already own a reusable `StubHandler`. Note it loops `while(true)` with `Task.Delay(500)` and no
cancellation, so a test must inject a handler that succeeds, or it hangs the run (the existing PRD
flags this exact hazard).

### M8 — Gate 🟡 *(partially done)*

**Done:** `coverage.yml` is committed at the repo root with the agreed policy — fixed 80% project
target, 80% patch target, zero tolerance on both, blocking on.

**Not done:** nothing is enforcing yet, and the settings panel is untouched (see the UI bug below).

`coverage.yml` is committed at the repo root. **It does not gate PR #84** — the service reads gate
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
| Seams alter production behaviour | High | Mitigated by design — every seam is an optional internal parameter defaulting to today's implementation; the 381 existing tests are the check |
| 80% unreachable without the invasive work | Medium | Open until M6; estimated landing ~86%, ~6 points of margin |
| Dedup changes behaviour (the copies are not identical) | Medium | Open — M1 names the two real deltas to preserve |
| `while(true)` poll in `WaitForAngularCliServerToAcceptRequests` hangs the suite | Medium | Open — M7 must inject a succeeding handler |
| Gate saved as `fixed` with null target abstains silently | Low | Open — M8; verify the check reports pass/fail, not `skipping` |
| `ExcludeByFile` not matching `Inject.g.cs` | Low | Pre-existing, 12 lines, immaterial |
