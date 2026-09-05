# Plan: `Response.Clear()` destroys every response header set upstream of prerendering

Implementation plan for [PRD-Prerendering-Response-Headers.md](./PRD-Prerendering-Response-Headers.md)
([issue #81](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81)).

Branch: `bugfix/prerendering-response-headers`, from the merged `master` (`0cf312b`).

**One PR.** Everything below — the fix, the spikes' outcomes, the inverted test, the hardening items,
and the docs — lands together.

## Milestones

### M1 — Investigation ✅ Complete

Four agents in parallel, no production code written.

| Agent | Scope | Outcome |
|---|---|---|
| **A** | Issue #81 + the local code path | Full issue text and both maintainer comments captured. Located the single `Clear()` at `SpaPrerenderingExtensions.cs:653`, mapped the whole pipeline, and **confirmed Microsoft provenance**: verbatim at line 238 of the file as first imported in `6acb640`, .NET Foundation header intact, never touched on its own merits. |
| **B** | ASP.NET Core semantics, cited to source | Quoted `ResponseExtensions.Clear`. Established the **eager-vs-`OnStarting` table** that explains the whole symptom, quoted `HstsMiddleware.Invoke` proving HSTS is eager, and produced the Group A / Group B split with a concrete failure mode per header. Found the `ResponseCompression`/`ResponseCaching` "invalidate, don't clear" idiom. |
| **C** | `ImprovedHstsMiddleware` | Hypothesis **confirmed**: line 95 is an `OnStarting` write and that timing is the package's entire reason to exist; everything else is deliberately identical to the framework. Found the mirror test on the Tools side and the second clobbering path in `SpaProxy.cs:173-194`. |
| **D** | Test-suite survey | `PrerenderingHarness.Run(...)` drives the real `UseSpaPrerendering` node-free; `SpaPrerenderingReflection.ServePrerenderResult` calls the serving path directly. Identified `Clears_headers_and_status_written_by_the_inner_middleware` as the test that pins today's behaviour and must be inverted. |

Two findings that changed the framing, both unprompted:

- **The body premise in the issue is wrong.** The `MemoryStream` swap at `:155-172` already discards
  the captured `index.html`; `Response.Body` is back to the original non-seekable stream by the time
  `Clear()` runs, so its `SetLength(0)` is a **silent no-op**. `Clear()` is solving a *header*
  problem only. This is what makes targeted invalidation viable at all.
- **`OnStarting` callbacks survive `Clear()`**, which is simultaneously why `ImprovedHstsMiddleware`
  works and why the snapshot-and-restore option (O2) carries a duplicate-header hazard that O1 does
  not.

### M2 — PRD + plan ✅

This document and the PRD.

### M3 — Design decisions ✅ Complete

The four spikes were resolved in a design interview with @PieterjanDeClippel rather than by
prototyping. **All nine decisions and their reasoning are in the PRD's *Decisions taken* table** —
that is the authoritative record; this section only lists what each former spike became.

| Former spike | Outcome |
|---|---|
| **1 — shape + drop-set** | Shape decided (**O3**). The drop-set *contents* and the options contract are the only things still to settle, and they are now M4a below rather than a spike. |
| **2 — `Cache-Control`** | **Preserved by default.** `StaticFileMiddleware` sets no `Cache-Control`, and dropping it would make success criterion 3 require configuration. One verification remains: confirm against `StaticFileContext.ApplyResponseHeaders` — folded into M4a. |
| **3 — `HasStarted`** | **Guard, log, throw clearly.** Believed unreachable since `SupplyData` became `Func<Task>`; the guard exists so that if it ever fires it is loud. |
| **4 — status + gate + `SkipPrerendering()`** | Fully decided: status preserved, gate splits on 3xx-with-`Location` plus explicit 304, body-less statuses emit no body, `SkipPrerendering()` **kept but re-documented**, `OnPrepareResponse` **deleted**. |

### M4a — Settle the drop-set and options contract ⬜

The one piece of design left. Not a spike — a short piece of desk work whose output is a decision
record, done before any test is written.

- Write out the drop-set and justify each name against RFC 9110 §8 (representation metadata) in one
  line: `Content-Length`, `Content-Type`, `Content-Encoding`, `Content-Language`, `Content-Range`,
  `Content-Location`, `Content-MD5`, `Accept-Ranges`, `ETag`, `Last-Modified`, `Transfer-Encoding`.
  Justify each **exclusion** too — most importantly the caching headers, per decision 4.
- Confirm the set is a superset of what `StaticFileMiddleware` sets on a 200 (check
  `StaticFileContext.ApplyResponseHeaders`), and confirm it sets no `Cache-Control`. Anything it sets
  that the drop-set misses is a leak of the old representation.
- Settle the options contract: property names, `ICollection<string>` vs `ISet<string>`,
  `StringComparer.OrdinalIgnoreCase`, and the same-name-in-both-collections case — recommendation:
  reject at startup rather than pick a precedence, since an unresolvable configuration is a bug.
- Decide where the built-in set is exposed read-only so consumers can see what they are adjusting.
- Framing-header validation (`Content-Length`, `Transfer-Encoding`, `Content-Range` in
  `PreserveResponseHeaders`) goes in the existing `UseSpaPrerendering` guard; the exception message
  carries the *why*, not just the name.

**Deliverable.** `docs/SOLUTION-prerender-header-invalidation.md`, also recording O1 and O2 as
rejected and why.

### M4b — Reproduction tests (red before green) ⬜

Written against current code and **verified red** before any fix lands. Extends
`Prerendering\SpaPrerenderingExtensionsTests.cs` (unit, via `SpaPrerenderingReflection.ServePrerenderResult`)
and `Prerendering\SpaPrerenderingMiddlewareTests.cs` (end-to-end, via `PrerenderingHarness.Run`), in a
new `ResponseHeaderPreservationTests` class.

**Headers**

| # | Case | Expected after fix |
|---|---|---|
| 1 | **Headline.** Real `HstsMiddleware` via `app.UseHsts()` upstream, HTTPS request | `Strict-Transport-Security` present |
| 2 | Eager upstream CSP + `X-Frame-Options` + `Referrer-Policy` | all three present |
| 3 | Upstream `Cache-Control: no-store` | present, **on defaults alone** |
| 4 | Static file sets `ETag` + `Last-Modified` | absent |
| 5 | Static file sets `Content-Length` for `index.html` | absent, or equal to the SSR byte count — never the old value |
| 6 | Static file sets `Content-Type: text/html; charset=…` | whatever `ServePrerenderResult` sets, not the captured value |
| 7 | `Accept-Ranges` / `Content-Range` on the captured response | absent |
| 8 | `Set-Cookie` set eagerly upstream | preserved |
| 9 | `UseResponseCompression` upstream | exactly one `Content-Encoding`, one `Vary: Accept-Encoding` |
| 10 | Unknown application header `X-Whatever` | preserved — nothing enumerates what is kept |
| 11 | Redirect branch with upstream security headers | `Location` + status correct **and** headers preserved |

**Status and gate**

| # | Case | Expected after fix |
|---|---|---|
| 12 | **Headline.** `OnSupplyData` sets 404 directly, no `OnStarting` | SSR page rendered **and** status 404 |
| 13 | `OnSupplyData` sets 403 directly | rendered body + 403 |
| 14 | `OnSupplyData` calls `Response.Redirect(url)` directly | pass-through, no SSR, `Location` + 302 |
| 15 | `OnSupplyData` sets **304** | pass-through, **no body**, no SSR |
| 16 | `OnSupplyData` sets **204 / 205** | pass-through, **no body** |
| 17 | `PassThroughAsync` reached with a 304 and a **full** buffer | no body written — the pre-existing bug |
| 18 | 3xx **without** `Location` (300) | prerendered — deliberate |
| 19 | `renderResult.StatusCode` alongside an `OnSupplyData` status | render result wins |
| 20 | Legacy pattern — status set inside `OnStarting` | still reaches the client |
| 21 | `SpaRouteService.Redirect`, rewritten to one line | `Location` + 301, SSR skipped |
| 22 | HEAD | unchanged — regression guard for the empty-buffer accident |

**Configuration and hygiene**

| # | Case | Expected after fix |
|---|---|---|
| 23 | **Zero configuration** | tests 1-22 pass on defaults alone |
| 24 | `PreserveResponseHeaders` rescues a dropped header | preserved |
| 25 | `DropResponseHeaders` drops a kept header (`X-Internal`) | dropped |
| 26 | `PreserveResponseHeaders` contains a framing header | throws at **startup**, message names header + reason |
| 27 | Same name in both collections | throws at startup |
| 28 | Header names differing only in case | matched ignore-case, both directions |
| 29 | Response already started at serve time | logged **and** throws, message names prerendering |
| 30 | Dev path — prerendering over `SpaProxy` | behaves; now-surviving dev-server headers pinned |

**Invert the existing pin.** `ServePrerenderResultTests.Clears_headers_and_status_written_by_the_inner_middleware`
(`SpaPrerenderingExtensionsTests.cs:376-388`) currently asserts `X-Inner` is gone. It becomes a test
that an unknown upstream header is **preserved**, with a comment citing issue #81 so the reversal is
not later "fixed" back. Also update the `SkipPrerendering()` test at
`SpaPrerenderingMiddlewareTests.cs:690-698`, whose comment explains the API by the defunct mechanism.

Record the failing count before M5 and the passing count after — red-before-fix verified, not assumed.

### M5 — Implementation ✅ Complete

Branch `bugfix/prerendering-response-headers`.

**`SpaPrerenderingExtensions.cs`**
- `Response.Clear()` → `DropTemplateHeaders`, removing only the precomputed drop-set. Carries a
  "do not reintroduce `Response.Clear()`" note with the reasoning and the `ResponseCompressionBody`
  precedent.
- `HasStarted` guard with an exception that names prerendering as the cause.
- `BuildHeadersToDrop` — validation plus `defaults ∪ Drop \ Preserve`, computed once at startup and
  captured in the middleware closure.
- Gate at the old `:388` is now `IsRedirect(context) || !CanHaveResponseBody(context) || IsPrerenderingSkipped()`.
- New `IsRedirect` — 3xx **and** a `Location`, so 304 falls to the body-less rule and a locationless
  300 is still rendered.
- `PassThroughAsync` — the buffer copy is gated on `CanHaveResponseBody`, fixing the pre-existing
  304-emits-a-body defect.
- Deleted: the `Response.OnStarting`/`OnPrepareResponse` registration, and `IsSuccessStatusCode`,
  which the gate change left unused.

**`SpaPrerenderingOptions.cs`** — `DefaultDroppedResponseHeaders` (11 headers), plus
`PreserveResponseHeaders` / `DropResponseHeaders` as get-only ordinal-ignore-case sets.
`OnPrepareResponse` deleted.

**`PrerenderingHttpContextExtensions.cs`** — kept; XML docs rewritten around the deliberate SSR
opt-out (crawler-only rendering, kill switch) instead of the defunct `OnStarting` visibility problem.

**`SpaRouteService.cs`** — both `Redirect` overloads collapse from eight lines to
`context.Response.Redirect(url, permanent: true);`. `permanent: true` kept, comment re-justified
semantically.

**Demo** — `UseResponseCaching`/`UseHsts`/`UseWebMarkupMin` moved to the top level before
`UseSpaImproved`; the `OnPrepareResponse` hook replaced by an ordinary header assignment in
`OnSupplyData`; both 404s simplified from `OnStarting` callbacks to direct assignment.

**Tests** — harness gained `configureServices`, `configureUpstream`, `onSupplyData` and
`locationFromOnSupplyData`; `RecordingNodeServices` gained `RedirectUrl`/`StatusCode`.

Two test-infrastructure defects found and fixed while establishing the baseline:

1. **`OnStarting` callbacks were silently dropped.** `HttpResponseFeature.OnStarting` is a no-op
   stub, so every test about deferred writes passed or failed for the wrong reason. Replaced with
   `CallbackFiringResponseFeature`, which fires them LIFO at the harness's flush point.
2. **The harness's bail-out sentinel was a locationless 302**, which the new gate correctly no longer
   treats as a redirect — so 25 existing tests started reaching `ExplodingNodeServices`. The sentinel
   now carries a `Location`, which is what a real redirect looks like.

Two existing tests were inverted, both with a comment saying so and why:
`ServePrerenderResultTests.Clears_headers_and_status_written_by_the_inner_middleware` →
`Keeps_...`, and `RedirectTests.Does_not_touch_the_response_until_it_starts` →
`Writes_the_redirect_immediately_rather_than_deferring_it`.

One of my own tests was wrong and was rewritten:
`Does_not_duplicate_a_header_that_a_deferred_callback_also_writes` simulated a middleware that both
set `Vary` eagerly *and* concatenated in `OnStarting` — which duplicates regardless of what this
middleware does. Replaced with `Preserves_a_header_exactly_once`, which pins the real property.

### M6 — Verification ✅ Complete

**Unit suite: 379 passed, 0 failed** (`--settings coverlet.runsettings --collect:"XPlat Code Coverage"`).

Red-before-green, verified rather than assumed:

| Point | Result |
|---|---|
| Baseline, before any implementation | **19 failed, 372 passed, 391 total** — all 19 in the new class, no existing test disturbed |
| After implementation | 0 failed, 379 total (12 fewer: the `IsSuccessStatusCode` tests went with the method) |

Of the 28 new tests, 9 were green at baseline for the wrong reason — absence assertions that passed
because `Clear()` removed everything. They are regression guards for the targeted removal, not
evidence the bug was reproduced, and the class doc-comment says so.

**End to end against `Demo.Web`**, Production, real Kestrel over HTTPS, real node prerender:

```
$ curl -sk -D - -H "Host: example.com" https://localhost:5001/person
HTTP/1.1 200 OK
Content-Length: 23249
Content-Type: text/html
Strict-Transport-Security: max-age=2592000      ← the headline criterion
Whatever: Oasis                                  ← set the ordinary way in OnSupplyData
X-HTML-Minification-Powered-By: WebMarkupMin
```

- **Success criterion 1 met with the framework's own unmodified `UseHsts()`.** Without the `Host`
  override the header is correctly absent, because `localhost` is in `HstsOptions.ExcludedHosts` —
  framework behaviour, not this bug.
- `/` still redirects: `301 Moved Permanently`, from a `SpaRouteService.Redirect` that no longer
  defers or calls `SkipPrerendering()`.

**The WebMarkupMin hypothesis is confirmed.** Moved to a top-level middleware before `UseSpaImproved`
and it works — and it is the better end state predicted in the PRD: the response is 23249 bytes on
**zero newlines**, with unquoted attribute values (`class=text-nowrap`) inside Angular-rendered
markup (`ng-server-context`, real person rows). So the **SSR output** is minified, not just the
template as in the old inside-the-SPA-callback position.

### M6b — Redundancy review of the recent PRs ✅ Complete

Asked by @PieterjanDeClippel: are the changes from the last few days' PRs now obsolete, since they
were made "more or less for the same reason"? Three agents — a catalogue of every behavioural change
in #82/#79/#76, a map of every guard now in the middleware, and an **empirical removal test of all 15
candidates** (delete one, rebuild, run the suite, restore).

**Answer: no. Nothing is redundant.** Full findings and the per-guard table are in the PRD under
*"Are the recent PRs' changes now obsolete?"*. The genuinely obsolete pieces — `SkipPrerendering()`
in `SpaRouteService.Redirect`, `IsSuccessStatusCode`, `OnPrepareResponse`, and the unconditional
buffer copy — were already removed by M5.

Two test-quality defects found by the exercise and **fixed**:

1. `Does_not_prerender_a_capture_with_a_content_encoding` did not exercise the `Content-Encoding`
   gate — its payload was rejected two guards earlier by the NUL and UTF-8 checks. Added
   `Does_not_prerender_a_capture_that_declares_an_encoding_but_decodes_cleanly`, an ASCII-clean body
   declared `Content-Encoding: br`.
2. Test case 29 (`HasStarted`) was planned in M4b and never written — the only guard in the whole
   middleware that no test defended. Added
   `Fails_with_a_named_error_when_the_response_has_already_started`, which needed a settable
   `HasStarted` on the test response feature, since the framework stub hard-codes `false`.

**Suite: 381 passing** (was 379).

### M7 — Docs + PR ⬜ Remaining

- [x] `SOLUTION-prerender-header-invalidation.md` — drop-set, options contract, `StaticFileMiddleware`
      source audit confirming the drop-set is a strict superset of the six headers it emits and that
      it sets no `Cache-Control`.
- [x] README — header contract, status contract, `UseHsts()` now works, caching-header risk,
      middleware ordering.
- [x] Demo updated to demonstrate the new idioms.
- [x] `SOLUTION-prerender-status-contract.md`.
- [x] Release notes — `RELEASE-NOTES.txt` for Prerendering (breaking changes, fixes, new options) and
      Routing (the `Redirect` simplification). All six packages bumped 10.7.1 → **10.8.0** in
      lockstep, per the repo's convention.
- [ ] Reply on issue #81.
- [ ] Open the PR.

## Risks

| Risk | Status |
|---|---|
| Drop-set under-enumerated → stale representation header leaks | **Closed.** Source audit confirms `StaticFileMiddleware` emits six headers; the drop-set of eleven is a strict superset. |
| Caching headers preserved in the wrong direction → cache poisoning | **Accepted, documented.** README warns; `DropResponseHeaders` is the opt-out. The one open hole is `StaticFileOptions.OnPrepareResponse`, which can set anything. |
| An app relied on `Clear()` wiping something | Behaviour change is the point; release notes. |
| `OnPrepareResponse` deletion breaks a consumer | Genuine breaking change; release notes name the replacement. |
| Gate split changes pass-through behaviour unexpectedly | **Closed.** Every branch pinned, and the full suite is green. |
| `SkipPrerendering()` kept with zero in-repo callers | Docs rewritten to carry the justification. |
| Double HSTS emission alongside `ImprovedHstsMiddleware` | Documented in the README. |
