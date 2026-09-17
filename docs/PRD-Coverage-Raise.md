# PRD: Raising coverage from 64.4% to 80%, and gating it

## Overview

Take line coverage over the six shipped libraries from **64.4%** to at least **80%**, and turn on the
coverage gate so it cannot slide back.

This is the successor to [PRD-Code-Coverage.md](./PRD-Code-Coverage.md), which built the pipeline and
the first test suite (20.8% → 48.8%). It lands on the same branch and in the same pull request as the
.NET 11 upgrade ([PRD-Dotnet-11-Upgrade.md](./PRD-Dotnet-11-Upgrade.md)).

## Problem Statement

Coverage has flattened. The measured progression on the coverage server:

| Commit | Coverage | Δ |
|---|---|---|
| `29db888` | 60.0% | +11.3 |
| `0cf312b` | 63.7% | +3.7 |
| `51cb55f` (master) | 64.4% | +0.8 |
| `f7f925c` (this branch) | 64.5% | +0.1 |

Large early gains, then a stall — the classic shape of a suite that has exhausted the cheap,
directly-callable logic and hit a wall of code with no test seam.

**718 lines are uncovered. 644 of them (90%) sit in nine files, and every one is blocked by exactly
two things: `Process.Start` and `new ClientWebSocket()`.**

| Uncovered | File |
|---|---|
| 190 | `NodeServices/HostingModels/OutOfProcessNodeInstance.cs` |
| 87 | `SpaServices/Npm/NodeScriptRunner.cs` |
| 87 | `SpaServices.Prerendering/Internals/NodeScriptRunner.cs` |
| 68 | `SpaServices/AngularCli/AngularCliMiddleware.cs` |
| 63 | `NodeServices/HostingModels/HttpNodeInstance.cs` |
| 50 | `SpaServices/Proxying/SpaProxy.cs` (the WebSocket half) |
| 36 | `SpaServices/Npm/ProcessTracker.cs` |
| 36 | `SpaServices.Prerendering/Internals/ProcessTracker.cs` |
| 27 | `SpaServices.Prerendering/AngularPrerendererBuilder.cs` |

Two compounding problems sit on top of that:

1. **The same code is dark twice.** Five files exist as near-identical copies in `SpaServices` and
   `SpaServices.Prerendering`. `ProcessTracker.cs` differs by exactly one line (the namespace);
   `NodeScriptRunner.cs` differs only in header, usings and formatting. That is **251 uncovered
   lines produced by roughly 125 lines of distinct logic**, and every seam built to test one copy
   would have to be built again for the other.
2. **Nothing stops regression.** The `coverage/project` and `coverage/patch` checks report
   `skipping`, because `Blocking` is off in the repository's gate — with it off, every verdict is
   forced to `neutral`. The gate has an 80% goal drawn on its chart and no field configured.

### What is *not* a problem

The premise that test projects inflate the denominator is **already false**.
`MintPlayer.AspNetCore.SpaServices.Tests` appears zero times in the Cobertura report, and the Demo
apps are absent too — the denominator is exactly the six shipped libraries. No exclusion list is
needed, and adding one would contradict NFR-3.1 of the existing PRD ("No assembly exclusion list
SHALL be used to inflate the percentage").

## Proposed Solution

### Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Deduplicate the five shared files | **Yes** — one implementation | Halves the cost of every seam below |
| How the shared code stays hidden | `internal` + `InternalsVisibleTo` | Genuinely invisible, not merely undiscoverable — see below |
| Where it lives | `MintPlayer.AspNetCore.SpaServices` | Smallest honest diff; Prerendering already references it |
| `TestServer` / `WebApplicationFactory` | **No** | Unlocks <20 lines — see below |
| Restructure `OutOfProcessNodeInstance` | **Out of scope** | 190 lines, the only genuinely invasive item; 80% is reachable without it |
| Gate | Fixed target 80%, patch 80%, blocking on | Committed as `coverage.yml`, reviewable and version-controlled |

### Why `internal` + `InternalsVisibleTo`, and not public

Making the shared classes public was considered and rejected. There is **no attribute that hides a
public type from outside consumers** — `[EditorBrowsable(Never)]` only hides from IntelliSense and
leaves the type fully usable; `[Obsolete(error: true)]` and `[Experimental]` enforce but misdescribe
the intent and still occupy the public API.

The decisive argument is forward compatibility: **these are precisely the classes this plan
reshapes.** `NodeScriptRunner` gains a constructor overload and an `IProcessLauncher` seam. Behind
`internal` that is free; on a public type shipped in `11.0.0-rc.1` it is a breaking change. The
packages are unsigned, so `InternalsVisibleTo` needs no public key — the same mechanism already used
to give the test project access.

Recorded caveat: `InternalsVisibleTo` is assembly-wide, so it grants Prerendering access to *all* of
SpaServices' internals, not just these five classes. Not a public leak, but a widening of the seam
between the two packages.

### Why not `TestServer`

There is no `TestServer`, `WebApplicationFactory` or `Microsoft.AspNetCore.Mvc.Testing` anywhere in
the solution, and adding one would not help. The dark code is **outbound** — spawning node, dialling
out with `ClientWebSocket` and `HttpClient`. `TestServer` replaces the **inbound** server. It would
put a fake socket in front of code already at 98% and leave every outbound call exactly as
untestable as it is today.

What it would nominally reach is already covered by the existing `PrerenderingHarness`, which builds
a real `ApplicationBuilder` over a real pipeline. It would also cost a real capability: the current
harness can hand-craft a 206 with a `Content-Range`, a truncated body, invalid UTF-8, or a mid-flight
abort — several of which are awkward or impossible through a Kestrel-shaped pipeline.

### The denominator moves, and that must be said out loud

Deduplication removes 123 uncovered lines **and 65 already-covered ones**:

| Pair | Copy A | Copy B |
|---|---|---|
| `NodeScriptRunner.cs` | 0/87 | 0/87 |
| `ProcessTracker.cs` | 0/36 | 0/36 |
| `EventedStreamReader.cs` | 54/54 | 54/54 |
| `EventedStreamStringReader.cs` | 12/12 | 11/11 |
| `LoggerFinder.cs` | 0/5 | 3/3 |

Net effect: **1301/2019 (64.4%) → 1236/1826 (67.7%)**, a 3.3-point rise from deleting duplicated
code rather than from testing anything.

NFR-3.2 of the existing PRD says the number "must be *true* before it is high". Banking 3.3 points
quietly would violate the spirit of that, so this PRD states it plainly: **roughly a fifth of the
journey from 64.4% to 80% comes from deleting duplicate code, not from new tests.** It is a genuine
improvement — half as much code, one place to fix a bug — but it is not new verification.

## Requirements

### Functional

- **FR-1.1** The five duplicated files SHALL exist as one implementation, `internal`, with
  `InternalsVisibleTo` granting Prerendering access. No public API change.
- **FR-1.2** Line coverage over the six shipped libraries SHALL be at least **80%**.
- **FR-1.3** `coverage.yml` SHALL configure: `projectMode: fixed`, `projectTarget: 80`,
  `projectThreshold: 0`, `patchTarget: 80`, `patchThreshold: 0`, `blocking: true`.
- **FR-1.4** New seams SHALL be `internal` and default to the real implementation, so that no
  consumer-visible behaviour changes.

### Non-functional — inherited from PRD-Code-Coverage and still binding

- **NFR-1.1** No test may require node, npm, network, a database or a real web server.
- **NFR-1.2** No test may sleep for a fixed delay; the suite stays well under a minute.
- **NFR-3.1** No assembly exclusion list, and no `ExcludeByAttribute`.
- **NFR-3.2** The number must be true before it is high. Coverage SHALL NOT be raised by shrinking
  the denominator except where that shrink is itself a genuine improvement, stated explicitly.
- **NFR-4.1** *(new)* No production behaviour change. Every seam defaults to today's implementation,
  and the existing 381 tests SHALL pass unchanged.

  **Amended mid-flight, deliberately.** The work stalled at 78.04% because a defect in
  `EventedStreamReader` made a whole class of tests hang the runner — and the same defect hangs the
  prerenderer in production. Holding NFR-4.1 would have meant shipping a known hang in order to keep
  a self-imposed rule about not changing behaviour. The exception is narrow and explicit:
  `EventedStreamReader` gains a bounded line history and an optional `CancellationToken`, covered by
  `WaitForMatchSequenceTests`. Everything else in this PRD still holds — no other shipped behaviour
  changes, and the two other defects found (`//thing` route composition, the unbounded readiness
  poll) remain recorded and unfixed.

## Out of scope

Deliberately not done — these are not deferred bookkeeping, they are judged not worth it:

- **`OutOfProcessNodeInstance` (190 lines).** Its constructor writes a temp file, builds a
  `ProcessStartInfo`, launches node, starts a `FileSystemWatcher` and connects stdio. Nothing can be
  constructed in a test without an `INodeProcess` abstraction and a restructured constructor. It is
  the only genuinely invasive item on the list, and **80% is reachable without it.**
- **`HttpNodeInstance` (63 lines).** Blocked twice over — it derives from the above, and news up its
  own `HttpClient`.
- **`ProcessTracker`'s Win32 layer.** The static constructor creates a Job Object and subscribes to
  `ProcessExit`; exercising it risks killing the test host. The `KillTrackedProcesses` loop could be
  reached behind an `ITrackedProcess` interface, but it is ~15 lines for a new abstraction.
- **Reversing the reflection-over-widening decision.** The existing PRD chose `InternalsVisibleTo`
  plus reflection helpers over widening members. Two widenings *are* proposed below where they
  directly unlock coverage; the rest stay as they are.
- **An `ignore:` key in `coverage.yml`.** Discussed in the service's roadmap but not implemented —
  it would be silently swallowed.

## Risks

| Risk | Note |
|---|---|
| The gate turns PR #84 red | Intended. `coverage.yml` is read from the **base ref**, so it does not judge the PR that introduces it — it starts enforcing on the first PR after merge. The red will come from the settings panel if that is switched on instead. |
| Seams change production behaviour | Mitigated by FR-1.4: every seam is an optional `internal` constructor parameter defaulting to the real implementation. |
| 80% proves unreachable without the invasive work | Estimated landing point is ~86%, with ~6 points of margin. If the estimate is wrong, the honest response is to lower the target, not to add exclusions (NFR-3.1/3.2). |
| `ExcludeByFile` is not actually working | Pre-existing: two `Inject.g.cs` files (12 fully-covered lines) still appear. The server drops them, so local and server numbers disagree slightly — the exact divergence the setting was added to prevent. Worth fixing; immaterial to the target. |
