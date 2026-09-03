# Plan: Corrupt / empty `originalHtml` in `UseSpaPrerendering`

Implementation plan for [PRD-Prerendering-Aborted-Requests.md](./PRD-Prerendering-Aborted-Requests.md).

Branch: `bugfix/prerendering-aborted-requests`. **Single PR** — both defects, the tests, and the
empty-template guard land together, and it supersedes PR #78.

## Milestones

### M1 — PRD + plan ✅

This document and the PRD.

### M2 — Investigation team (spikes 1-5)

A team of four investigates in parallel; nobody writes production code in this milestone. Output is
answers written back into the PRD's spike table, plus whatever throwaway harness code proves them.

| Agent | Scope | Deliverable |
|---|---|---|
| **A** | Spike 1 — middleware testability | A working xunit test that builds the `UseSpaPrerendering` delegate, invokes it against a `DefaultHttpContext`, and captures `originalHtml` through a fake `ISpaPrerenderingService` that then sets a 302 to bail out before node. |
| **B** | Spike 2 — `GetBuffer()` semantics | Empirical answer on padding content, padding length, truncation reachability, and BOM. Retires or confirms the report's "garbage / truncated" claim. |
| **C** | Spikes 3+6 — abort contract | The exact ASP.NET Core behaviour on abort, cited to source, and what the middleware's own outbound calls do on an aborted request. |
| **D** | Spikes 4+5 — real app | Baseline `dotnet run` of `Demo/Prerendering/Demo.Web` prerendering successfully, then the dev-server-proxy vs. static-files question, then a forced-abort repro with the observed Angular error. |

### M2 — ✅ Complete

All five spikes answered; results in the PRD's spike table. **Both defects reproduced**, including a
real Chromium abort hitting NG05104 at a 5.5% rate in Production — the reproduction the upstream
reporter could not produce.

### M3 — Reproduction (goals G1-G3)

Model the **static-files** contract — that is the one that actually fires (status set →
content-type set → empty body → no throw). Add the proxy contract only as a *negative* assertion
("`ContentType == null` ⇒ no prerender, no node call"), which locks in the behaviour that makes
development safe, plus optionally the proxy's mid-copy truncation case.

Turn A's harness plus B/C's findings into committed tests that **fail on `master`**:

- `originalHtml` equals the response body exactly — no padding, no `\0`. **The template must be
  >16 KB, <256 bytes, or written in ≥2 chunks** — see the PRD's test-design trap; a realistic
  small template makes this assertion pass on `master` with the bug present. Audit
  `SpikeHarnessTests.cs` for exactly this. Its Defect-1 test **does** fail on `master` (76-byte
  template → capacity 256 → asserts `new string('\0', 180)`), but it dodges the trap **by accident**
  — it lands in the `<256`-byte branch, not the realistic one. Add a **>16 KB single-file case**
  (e.g. 20,000 bytes → capacity 32768, 12,768 NULs, written as `16384 + 3616`), which is the shape a
  real `ng build` `index.html` has in production and the only one that exercises the multi-write
  growth path.
- A UTF-8-BOM `index.html` does not put U+FEFF at the head of the template (Defect 1b).
- An aborted request does not reach `OnSupplyData` / does not invoke the prerenderer.

Both go in `MintPlayer.AspNetCore.SpaServices.Tests/Prerendering/`. Commit the failing tests
before the fix so the diff shows red → green.

### M2b — Cancellation-token audit (spikes 7-8, Workstream 3)

Ran alongside M2 with a fifth agent, as **independent work** — its findings stand on their own
merits and must not be presented as part of either bug fix. ✅ **Complete.**

The gate is open: a cancelled RPC cannot be retried or misread as a dead node instance. The
outcome is a *narrow* change, not a sweep — **one** genuinely wrong callsite (`Prerenderer.cs:76`),
with `applicationStoppingToken` left strictly alone (linking it would delete the shared
`prerenderer.js` temp file for the whole process — see the PRD). Cosmetic response-write callsites
are optional and explicitly excluded on the abort path. G8: document the token-less overloads, do
not obsolete them.

Deliverable for M5: add the request token parameter, link it in the middleware in the
`SpaProxy.cs:58-61` shape, plus a cancellation-specific test asserting no retry occurs. Release
notes must mention that `SpaPrerenderingExtensions.cs:168` can now throw `OperationCanceledException`
where it previously always ran to completion.

### M4 — Solution team

A second team, launched only once M3 is red-on-`master`, decides the *correct* fix for each defect
rather than defaulting to the reporter's patch. Questions it must answer, not just implement:

1. Decode strategy for Defect 1 — length-bounded `GetBuffer`, `ToArray`, `Span`, and whether to
   honour the response charset / strip a BOM instead of hard-coding UTF-8.
2. Abort-check placement for Defect 2 — standalone early return vs. folded into `canPrerender`;
   what the response should look like on that path; whether `RequestAborted` is even the right
   signal or whether an empty-buffer check subsumes it.
3. ~~Whether the G5 empty-template guard makes the `RequestAborted` check redundant.~~
   **Resolved by Spike 3: they do not overlap.** An abort mid-copy leaves a *partial* template,
   which is non-empty and passes any `IsNullOrWhiteSpace` guard while still breaking SSR. Only the
   `RequestAborted` check covers that case. Both earn their place.
4. Logging: does a skipped prerender log a warning? What logger is reachable here
   (`Internals/LoggerFinder.cs` exists — is it usable from the middleware)?
5. Workstream 3: which token each audited callsite gets, whether `Prerenderer.RenderToString`'s
   signature grows a request token or its existing `applicationStoppingToken` parameter is replaced
   by a pre-linked one, and the G8 public-API call on the token-less `INodeServices` overloads.

### M5 — Implement + verify

Apply M4's decisions, then a single test sweep (`dotnet test -c Release`) at the end — not per
milestone. Verify intermediate steps by reading code and building.

### M6 — PR

One PR against `master`, cross-referencing #78 and crediting @Reonekot in the body. Close #78 as
superseded (ask before closing someone else's PR).

## Decisions taken (user, this session)

| Question | Decision |
|---|---|
| Line-161 `OnStarting` bypass | **Add the `SkipPrerendering()` opt-out.** Additive extension in the Prerendering package, checked beside the line-161 re-check, called by both `SpaRouteService.Redirect` overloads. Third-party deferred status changes remain undetectable by design — document that. |
| PR #78 | **Absorb @Reonekot's commits onto our branch**, preserving git authorship, then stack our work on top. |
| `WithTimeout` fault wrapping | **Fix it** to `await task`. Update the two assertions at `Tests/Utils/TaskTimeoutExtensionsTests.cs:44-62` (`AggregateException` → `InvalidOperationException`) and delete the stale comment. |
| Extra scope | **All of it, plus anything else found along the way**: the `isBuildStarted` latch fix, the demo `RootPath` fix, the `SpaProxy` CTS disposal leak, and the dead-code deletions (`Prerenderer.cs:18` overload, the unreferenced Prerendering `TaskTimeoutExtensions.cs` + its 6 duplicate tests, the `buildTimeout` local at line 62). |
| `UseWebMarkupMin` placement | **Leave it exactly where it is.** Its current position is the only one where SSR and minification work together, and nothing in the chosen solution requires moving it — the rejected `ContentLength` heuristic was the only thing that would have, and it was rejected on general grounds (any consumer may transform responses inside `next()`), not because of the demo. |
| Build timeout option | No new public option. Keep `StartupTimeout` at 120s, read at the point of use, and **fix the wrong XML doc on `SpaPrerenderingOptions.TimeoutMilliseconds`**, which claims to be a build timeout but is the node RPC render timeout. |

## Open decisions for the user

- **Credit / mechanics for #78**: absorb the reporter's commits into our branch (preserving
  authorship) vs. our own commits plus a `Co-Authored-By` vs. reviewing and merging #78 and adding
  tests on top. Third option keeps their authorship cleanest but the PRD's guard work would then
  need to ride along in the same PR anyway.
- **IIS**: PRD declares it out of scope. Confirm that is acceptable given the reporter runs
  production on it (unknown which).
