# PRD: A false cache-override warning on every cache-aware response, and no per-endpoint opt-out that keeps the rest of the pipeline

Upstream: [issue #88](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/88) — *"Xsrf: false 'headers overridden' warning on every response with its own Cache-Control, and no per-endpoint opt-out that keeps the rest of the pipeline"*.

## Overview

Two gaps in `MintPlayer.AspNetCore.SpaServices.Xsrf` 11.0.0-rc.2, both left over from the cache-header work in #86.

1. **A false Warning per request.** `DefaultAntiforgery.GetAndStoreTokens` logs EventId 8
   (`ResponseCacheHeadersOverridenToNoCache`) whenever the response already carries a caching policy, then
   overwrites it. Under the default `XsrfCacheHeaderPolicy.PreservePrivate` the package puts the application's
   policy back immediately afterwards, so the message describes headers the client never receives. It is
   logged once per request, at Warning, on every endpoint that declares a caching policy.
2. **No surgical opt-out.** Public, shared-cacheable endpoints (badges, feeds, OG images, `.well-known/*`) get
   a per-user `Set-Cookie`, have `public` forced to `private`, and pay a DataProtection round trip for a token
   nobody reads. The only documented escape, `.ShortCircuit()`, also skips authentication, authorization,
   CORS, rate limiting and output caching, and only works when the generator sits below `UseRouting()`.

Both changes are additive. Part 1 changes log output only. Part 2 adds public API and keeps the default.

## Verification: the issue's claims, checked

Measured in a throwaway spike (`spike88`, real Kestrel, `127.0.0.1:0`, a recording `ILoggerProvider`), on
**.NET 10.0.12 and .NET 11.0.0-rc.1.26425.128**. Both runtimes gave identical results.

### 1. The framework warns although the headers are restored — confirmed, and broader than stated

The issue says the trigger is "a parseable `Cache-Control` without `no-cache`". The measured trigger is wider:

| Headers present when `GetAndStoreTokens` runs | EventId 8 logged? |
|---|---|
| none | no |
| `public, max-age=60` / `private` / `no-store` | **yes** |
| `no-cache` alone | **yes** — `no-store` is required too |
| `no-cache, no-store` (± `Pragma: no-cache`) | no |
| `no-cache` + `Pragma: no-cache` | **yes** |
| `max-age="unterminated` (unparseable) | no |
| `Pragma: public` alone | **yes** — Pragma is a second trigger |
| `Pragma: no-cache` alone | no |
| app sets `public` + `Pragma: public`, both **removed** in `OnStarting` before the mint | **no** |

So the framework stays silent only when both headers are absent, when `Cache-Control` already holds both
`no-cache` and `no-store`, or when `Cache-Control` cannot be parsed. Hiding **both** headers silences every
case. The broader trigger does not change the fix. It does widen the set of responses the fix affects.

### 2. The suite cannot see it — confirmed

`XsrfTestHost.BuildServices` calls `.AddLogging()` with no provider, and `StubAntiforgery` never logs. The
only test that runs the real `DefaultAntiforgery` (`A_registered_pipeline_actually_writes_the_cookie`) does
not inspect logs.

### 3. `GetEndpoint()` is visible inside `OnStarting` from above `UseRouting()` — confirmed

| Request | endpoint in middleware body | endpoint in `OnStarting` | `GetMetadata<T>()` |
|---|---|---|---|
| `MapGet(...).WithMetadata(m)` | null | set | `m` |
| `MapGroup(...).WithMetadata(g)` → child | null | set | `g` |
| group(`g`) → child `.WithMetadata(i)` | null | set | `i` (innermost wins) |
| unmatched (404) | null | **null** | — |

The check must therefore run inside the callback, and a null endpoint must mean "mint" (today's behaviour).

## Goals

- G1. Under `PreservePrivate`, no Warning from `DefaultAntiforgery` for a response whose policy the package restores.
- G2. Under `NoStore`, nothing changes. The override is real there, and so is the warning.
- G3. A failed mint leaves the application's cache headers exactly as the application set them.
- G4. An application `Cache-Control` the package must drop (unparseable) is reported, once, by the package itself.
- G5. One endpoint, group or controller can be kept out of the mint without leaving the pipeline, wherever the
  generator is registered relative to `UseRouting()`.
- G6. Responses with no endpoint can be excluded by a predicate.
- G7. The default remains an unconditional mint.

## Non-goals

- Changing the unconditional-mint default (#86 recorded why).
- Skipping automatically on `Cache-Control: public` (see Decisions, D9).
- Turning off antiforgery **validation**. That is `DisableAntiforgery()` / `[RequireAntiforgeryToken(false)]`.

## Candidate solutions

### Part 1

- **O1. Hide the headers while minting** ← recommended. Under `PreservePrivate`, after the snapshot, remove
  `Cache-Control` and `Pragma`, mint, restore. The framework has nothing to inspect. The spike shows identical
  wire output on both TFMs.
- O2. Filter EventId 8 with a logging filter registered by the package. Rejected: it silences a true warning
  under `NoStore` and from other callers of `GetAndStoreTokens`, and a library should not rewrite the host's logging.
- O3. Ask applications to raise the `DefaultAntiforgery` category's minimum level. Rejected: it pushes the
  package's own side effect onto every consumer and hides real warnings from that category.

### Part 2

- **O4. Endpoint metadata (`ISkipXsrfTokenMetadata`, `[SkipXsrfToken]`, `.SkipXsrfToken()`), read in
  `OnStarting`** ← recommended. It sits next to the endpoint it describes, survives route moves, and leaves
  the rest of the pipeline alone.
- **O5. `XsrfOptions.ShouldIssue` predicate** ← recommended as the companion for responses with no endpoint.
- O6. `.ShortCircuit()`. Rejected as the recommendation for the reasons in the overview. It stays documented as an option.
- O7. `UseWhen` on the path. It works today but is path-based and drifts. It is mentioned in the README as the pre-rc.3 workaround.

## Decisions taken

The authoritative record.

| # | Decision | Outcome and reasoning |
|---|---|---|
| D1 | Name | `SkipXsrfToken`. It ties the opt-out to this package's cookie and does not echo `DisableAntiforgery`, which turns off *validation*. The XML docs say explicitly that validation is unaffected. |
| D2 | Metadata shape | `ISkipXsrfTokenMetadata { bool Skip { get; } }` + sealed `SkipXsrfTokenAttribute(bool skip = true)` (class or method) + `SkipXsrfToken<TBuilder>(bool skip = true)` on `IEndpointConventionBuilder`. Resolved last-wins through `GetMetadata<T>()`, which follows the framework's `IAntiforgeryMetadata.RequiresValidation` pattern, so an action or endpoint can re-enable the mint inside a skipped controller or group. *Revised in PR review: the first cut was a marker interface.* Adding a member to a public interface after GA would be breaking, so the bool is in place before rc.3. Matching on the interface also lets applications supply their own metadata type. |
| D3 | Where the check runs | Inside the `OnStarting` callback, before the snapshot or any header is touched. A skipped response is byte-for-byte what the application produced. |
| D4 | Order of checks | Metadata first, then `ShouldIssue`. The predicate can still see the endpoint. |
| D5 | A throwing `ShouldIssue` | Logged at Error with its own message, and the token **is issued** (fail open). *Revised in PR review: the first cut failed closed.* The two mistakes are not symmetric. Wrongly minting costs a `Set-Cookie` and a `private` downgrade, which is exactly the pre-rc.3 behaviour. Wrongly skipping the SPA's entry point or refresh endpoint breaks every mutation that follows. A buggy predicate throws on precisely the requests its author did not anticipate. |
| D6 | Failure path after hiding | When the mint throws, the hidden headers come back **verbatim**, with no `TryMakePrivate`, because no token reached the client. **Exception:** if the failed mint left a `Set-Cookie` behind (possible with a replaced `IAntiforgery`), the private-forcing restore runs instead, because a per-user cookie is now on the response. The added `Set-Cookie` is detected by count, which is cheap. |
| D7 | Unparseable `Cache-Control` | Behaviour unchanged: the mint's `no-cache, no-store` + `Pragma: no-cache` stay. New: one package Warning naming the dropped value, logged once per middleware instance, which in practice is once per process. This is the same mechanism as the insecure-cookie warning. |
| D8 | `NoStore` | Untouched. The framework's warning is true there and stays. |
| D9 | Auto-skip on `public` | Rejected. Prerendered SPA HTML can legitimately be `public` (#83), and skipping there breaks the first mutation. Only the application can tell the two apart. |
| D10 | Test vehicle for pipeline claims | A real loopback Kestrel inside the test suite. Kestrel ships in the shared framework, so no new package (TestHost) is needed, and the rate-limiter, authorization, controller and above-`UseRouting()` claims are measured on the real server rather than a simulated one. |
| D11 | Release | All six packages to `11.0.0-rc.3`. `build-master` pushes with `--skip-duplicate`, so an unbumped package is silently skipped. |

## Risks

| Risk | Mitigation |
|---|---|
| Skipping on the SPA's HTML entry point or a refresh endpoint silently breaks every later mutation | The README carries a hazard box. The default is unchanged and the opt-out is explicit per endpoint. |
| Applications relying on the mint's `X-Frame-Options: SAMEORIGIN` lose it on skipped endpoints | Documented. It is irrelevant for images and feeds; for HTML, set the header yourself. |
| Hiding the headers changes the failure path (#86 relied on "a throw leaves headers untouched") | D6. Tests cover a throw with an application value (verbatim) and a throw after a cookie was written (private). The rewritten comment states the new contract. |
| A replaced `IAntiforgery` that writes no cache headers | With nothing hidden there is no change. With headers hidden, `Apply` restores them as today. |
| Recording framework logs makes existing "no warning" assertions fragile | The new tests filter by category and EventId. The recorder gains both fields. |

## Success criteria

| # | Criterion |
|---|---|
| S1 | Under `PreservePrivate`, `public, max-age=300` / `private, max-age=300` / `no-store` / `no-cache` / Pragma-only produce no Warning from any category, with the same headers as rc.2. |
| S2 | Under `NoStore` the framework's EventId 8 **is** recorded. This proves the recorder is wired. |
| S3 | A throwing mint restores `public, max-age=300` verbatim with no `Set-Cookie`. |
| S4 | An unparseable `Cache-Control` logs the package Warning once across several requests, and `no-cache, no-store` stays. |
| S5 | `.SkipXsrfToken()` on a minimal API endpoint and on a group, plus `[SkipXsrfToken]` on a controller and on an action: no `Set-Cookie`, `public, max-age=300` unchanged, no `X-Frame-Options`. |
| S6 | The S5 tests pass with the generator registered **above** `UseRouting()`. |
| S7 | A skipped endpoint behind `UseRateLimiter` still gets 429 over the limit, and behind `UseAuthorization` still gets 401. |
| S8 | A neighbouring non-skipped endpoint and an unmatched (404) request still mint. |
| S9 | `ShouldIssue` returning false skips. A throwing `ShouldIssue` is logged, the token is issued, and the response survives. |
| S9a | `[SkipXsrfToken(false)]` on an action inside a skipped controller, and `.SkipXsrfToken(false)` on an endpoint inside a skipped group, both mint. |
| S10 | Reverse check: the S1 and S5–S7 tests fail against rc.2's implementation. |
| S11 | Full suite green on net10.0 and net11.0. Xsrf keeps 100% line and branch coverage. |
| S12 | A real-Kestrel before/after capture is in `docs/SOLUTION-xsrf-skip-token.md`. |

## Out of scope

- Changing the unconditional-mint default.
- The `UseSpaPrerendering` + `UseAntiforgeryGenerator` integration test #86 mentioned. That is a different concern.
