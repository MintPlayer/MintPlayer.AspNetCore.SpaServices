# Plan: Silence the false cache-override warning and add a per-endpoint `SkipXsrfToken` opt-out

Implementation plan for [PRD-Xsrf-Skip-Token](./PRD-Xsrf-Skip-Token.md) ([issue #88](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/88)).

Branch: `feature/xsrf-skip-token-and-silent-override`, from master (`f121800`).

**One PR.** Both parts, the tests, the README, the release notes and the six-package version bump land together.

## Milestones

### M1 — Investigation ✅ Complete

| Agent | Scope | Outcome |
|---|---|---|
| Middleware + tests | `AntiforgeryMiddleware.cs`, `XsrfOptions.cs`, `XsrfTestHost.cs` | The callback layout and the snapshot/restore contract. The test host is a `DefaultHttpContext` with a LIFO `OnStarting` feature and a stub antiforgery that never logs. The recorder has no category or EventId. TestHost is not referenced. |
| Docs + release | #86 PRD/PLAN/SOLUTION, README, RELEASE-NOTES, versions, coverage gate | The format to copy. Six csproj `<Version>`s at `11.0.0-rc.2`. Coverage is 95/90 overall/patch, and Xsrf has been held at 100%. |
| Framework spike | `DefaultAntiforgery` EventId 8 trigger; `GetEndpoint()` timing | See below. |

Findings that changed the framing:
- The warning trigger is broader than the issue says. It also fires for `no-cache` alone and for any `Pragma` other than `no-cache`. Hiding both headers still silences every case.
- An unmatched request has a null endpoint even in `OnStarting`, so it must mint.
- `GetMetadata<T>()` returns the innermost metadata, so the endpoint's own wins over its group's.

### M2 — PRD + plan ✅

### M3 — Spikes ✅ Complete

| # | Spike | Answer |
|---|---|---|
| S-A | When does `DefaultAntiforgery` log EventId 8, and does removing both headers in `OnStarting` before the mint silence it? | It warns for a parseable CC missing `no-cache` or `no-store`, and for a Pragma other than `no-cache`. Removal silences it. Identical on 10.0.12 and 11.0.0-rc.1. |
| S-B | Is endpoint metadata visible in an `OnStarting` callback registered above `UseRouting()`, for `MapGet` and `MapGroup`? | Yes. It is null in the middleware body and set in the callback. 404 has no endpoint. |

#### Spike definitions, as written before they were run

| # | Spike | Question | Vehicle |
|---|---|---|---|
| S-A | Framework warning | Trigger matrix and whether hiding the headers suppresses it | Minimal web app on Kestrel `:0`, recording logger, both TFMs |
| S-B | Endpoint timing | `GetEndpoint()` inside `OnStarting` from above an explicit `UseRouting()` | Same host |

**Deliverable:** the before/after capture goes to `docs/SOLUTION-xsrf-skip-token.md` (M8).

### M4 — Resolve open decisions ✅

The PRD's Decisions table (D1–D11) is the record.

### M5 — Reproduction tests ✅

- `XsrfTestHost`:
  - `Entry` gains `Category` and `EventId`.
  - `BuildServices` routes the recorder in.
  - `Run(frameworkAntiforgery: true)` resolves the real `DefaultAntiforgery` from that provider.
- Part 1 (`XsrfFrameworkWarningTests`):

  | # | Case | Expected after fix |
  |---|---|---|
  | 1 | `public, max-age=300` / `private, max-age=300` / `no-store` / `no-cache` / Pragma-only, `PreservePrivate`, real antiforgery | no Warning, headers as rc.2 |
  | 2 | same, `NoStore` | EventId 8 recorded |
  | 3 | throwing mint + `public, max-age=300` | restored verbatim, no `Set-Cookie` |
  | 4 | throwing mint that wrote a cookie first | restored, forced `private` |
  | 5 | unparseable CC, three requests | one package Warning naming the value; `no-cache, no-store` stays |

- Part 2, unit (`XsrfSkipTokenTests`, `DefaultHttpContext`):
  - endpoint with `SkipXsrfTokenAttribute` → no cookie, headers untouched, no `X-Frame-Options`
  - an application-supplied `ISkipXsrfTokenMetadata` also skips
  - endpoint without it, and no endpoint → mint
  - `ShouldIssue` false → skip; true → mint; throws → Error logged, token issued (fail open), the response survives
  - most specific metadata wins: `Skip = false` after `Skip = true` mints, and the reverse skips
  - `SkipXsrfToken()` on a builder adds the attribute and returns the builder; null builder throws
- Part 2, real Kestrel (`XsrfSkipTokenPipelineTests`, generator **above** `UseRouting()`):
  - `MapGet(...).SkipXsrfToken()`, a `MapGroup(...).SkipXsrfToken()` child, `[SkipXsrfToken]` on a controller and on an action
  - a neighbouring plain endpoint and a 404 still mint
  - `RequireRateLimiting` (1/window) still yields 429 on a skipped endpoint
  - `RequireAuthorization` still yields 401 on a skipped endpoint
- Reverse check: run Part 1 #1 and Part 2 against the rc.2 implementation (stash `AntiforgeryMiddleware.cs`) and record the red count.

### M6 — Implementation ✅

- `SkipXsrfToken.cs` (new): `ISkipXsrfTokenMetadata`, `SkipXsrfTokenAttribute`, `SkipXsrfTokenExtensions.SkipXsrfToken<TBuilder>()`.
- `XsrfOptions.ShouldIssue`: `Func<HttpContext, bool>?`.
- `AntiforgeryMiddleware.cs`:
  - skip checks at the top of `WriteCookie`
  - `CacheHeaderSnapshot.Hide` / `Reinstate`
  - the mint wrapped so a throw reinstates (D6)
  - `Apply` reports an unrestorable value → `WarnOnceIfDropped`
  - rewritten failure-path comment

### M7 — Documentation ✅

- README:
  - Public surface rows
  - new "Keeping an endpoint out of the mint" section with a hazard box
  - "Endpoint-routed static assets": `MapStaticAssets().SkipXsrfToken()`
  - Caching: no more override warning under `PreservePrivate`
- RELEASE-NOTES `v 11.0.0-rc.3`.
- All six `<Version>`s → `11.0.0-rc.3`.

### M8 — Verify ✅

- ✅ Full suite: 652 tests per TFM, net10.0 and net11.0.
  - The first run had 1 failure per TFM: the rate-limiter test expected 429, but the framework's default `RejectionStatusCode` is 503. The limiter itself had run.
  - The test host now sets 429 explicitly. The Xsrf tests were re-run: 79/79 on both TFMs.
- ✅ Coverage: Xsrf at 100% line and 100% branch, both TFMs.
- ✅ Reverse check:
  - Against rc.2's `AntiforgeryMiddleware.cs` (new tests kept, implementation reverted), 21 of 79 Xsrf tests fail on both TFMs: every part-1 warning test and every skip test.
  - `Restores_the_application_policy_verbatim_when_the_mint_throws` passes on rc.2 as well. That is expected, because rc.2 never hid the headers. The test guards the new failure path.
- ✅ Real-Kestrel before (nuget rc.2) and after (project ref) capture → `docs/SOLUTION-xsrf-skip-token.md`.
- New tests use [MintPlayer.Assertions](https://www.nuget.org/packages/MintPlayer.Assertions) (`11.0.0-rc.5`), at the maintainer's request. Existing test files are unchanged.

### M9 — PR ✅

[#89](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/89).

### M10 — Review feedback ✅

From the [review comment](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/89#issuecomment-5981490505):

1. **`ShouldIssue` now fails open.** A throwing predicate is logged and the token is issued (PRD D5).
2. **`ISkipXsrfTokenMetadata.Skip`, resolved last-wins** (PRD D2). `[SkipXsrfToken(false)]` and `.SkipXsrfToken(false)` re-enable the mint inside a skipped controller or group. Covered by unit tests and by Kestrel tests (`/public/page`, `/skipped-controller/page`).
3. **Capture provenance.** `docs/SOLUTION-xsrf-skip-token.md` was re-run against the committed PR head rather than a working tree.

## Decisions taken

See the [PRD](./PRD-Xsrf-Skip-Token.md#decisions-taken).

## Notes for whoever picks this up

- Batch test runs to M8. Redirect output, unfiltered, to a log file.
- The spike host lives in the session scratchpad (`spike88`). It is not committed.
