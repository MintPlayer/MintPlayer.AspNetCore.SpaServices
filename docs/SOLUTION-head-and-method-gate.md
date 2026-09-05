# Solution: the HEAD `Content-Length` regression, and a request-method gate

Scope: two items from `PRD-Prerendering-Range-Requests.md` — **G4** (the shipped HEAD
`Content-Length` regression and the benign-HEAD warning) and **G5** (should prerendering apply to
non-GET methods). Deliberately *not* in scope: `canPrerender`, `RemoveConditionalRequestHeaders`,
`ReadCapturedHtml`, and the empty-template guard's *rejection* logic — those belong to the
`Range`/206 workstream. Coordination points with that workstream are marked **[shared]**.

This document decides; it contains no production code.

---

## 1. Decision summary

| # | Decision |
|---|---|
| D1 | **Gate prerendering to GET.** A non-GET request is passed straight through: no bundle build await, no header surgery, no capture, no prerender. |
| D2 | **Place the gate after the excluded-URL loop and before the bundle build await.** That placement is what also removes the wasted `ng build` block. |
| D3 | **Also narrow the `PassThroughAsync` reconciliation** to responses that can carry a body (skip it for HEAD and for 204/205/304). The gate makes this unreachable for HEAD, but the reconciliation is wrong *in principle* for a bodyless response and 304/204 remain reachable on a GET. |
| D4 | **Keep the empty-template guard at Warning**, but rewrite its comment and add `{Method}`/`{StatusCode}` to the message. HEAD — the one benign cause we knew of — is gated away upstream, so the level is now defensible. |
| D5 | **No new public option.** No `PrerenderedMethods` / method predicate on `SpaPrerenderingOptions` until a consumer actually asks. |
| D6 | 10.7.0 is **superseded by 10.7.1, not pulled**. Severity is low-to-moderate and affects HEAD only. |

---

## 2. The HEAD `Content-Length` regression (G4)

### 2.1 What is actually happening

`PassThroughAsync` (`SpaPrerenderingExtensions.cs:271-279`) reconciles unconditionally:

```csharp
if (context.Response.ContentLength.HasValue && context.Response.ContentLength != outputBuffer.Length)
{
    context.Response.ContentLength = outputBuffer.Length;
}
```

For a HEAD, `StaticFileMiddleware` answers `200` + `text/html` + the **full** file
`Content-Length` and writes **no body** — which is exactly right. Our capture is therefore
legitimately empty, the empty-template guard fires, and the reconciliation then "corrects" a
correct `547` to `0`. Measured in the PRD: expected 547, actual 0.

The reconciliation's own doc-comment states its justification precisely: it exists for *"the
request's abort token cancelled while the connection is still alive"*, where Kestrel would otherwise
fail the response with `Response Content-Length mismatch: too few bytes written`. That justification
does not extend to a HEAD — Kestrel's `VerifyResponseContentLength` skips HEAD outright, so there is
nothing to reconcile away and the rewrite buys nothing while destroying real metadata.

### 2.2 Which shape to fix — both, and why

**The gate (D1) is the primary fix.** It is a fix at the source: a HEAD never enters the capture, so
the empty capture, the false-alarm warning and the `ContentLength` rewrite all cease to exist rather
than being individually patched. Three defects collapse into one three-line check. That is the
right altitude: the bug is not "the reconciliation mishandles HEAD", it is "the middleware has no
notion of which methods prerendering applies to" (PRD A4: `Request.Method` is never read).

**But the reconciliation is still wrong in principle, so narrow it too (D3).** Reasons, in order of
weight:

1. **The invariant is about the response, not about HEAD.** "Zero captured bytes contradicts a
   declared length" is only true for a response that *may* carry a body. For a bodyless response —
   HEAD, `204`, `205`, `304` — zero bytes is the correct and expected outcome, and a declared length
   is metadata about the representation, not a promise about this message. Rewriting it is a
   category error that happens to be observable on HEAD first.
2. **204/205/304 remain reachable on a GET, today.** `PassThroughAsync` is the destination for every
   non-prerenderable response. A `304` is unlikely from static files (we strip the conditional
   headers) but entirely reachable from a dev-server response copied by `SpaProxy`, or from a
   consumer's middleware inside the callback; `204`/`205` are reachable from any consumer endpoint
   registered inside the capture. The gate does nothing for those. (PRD Tier C already flags 204/205
   as "benign *only because* the empty guard fires" — that is exactly the population this narrowing
   protects.)
3. **The path the reconciliation serves is itself unusual.** By its own comment it insures a
   synthetic cancelled token on a live connection — a request-timeout feature or a linked token.
   Cheap insurance is fine; cheap insurance that mutates correct headers on a much more common path
   is not. Keeping the insurance and constraining it to the case it was written for costs one
   predicate.
4. **Defence against the gate being relaxed later.** If anyone ever adds an opt-in for a non-GET
   method (D5's escape hatch), the reconciliation must not silently reintroduce this bug.

Shape: a `CanHaveResponseBody(HttpContext)` predicate — not HEAD, and status not `204`/`205`/`304` —
guarding the existing `if`. It belongs inside `PassThroughAsync`, which already owns the
reconciliation and already has the context; the caller does not need to know.

### 2.3 RFC position

RFC 9110 §9.3.2: a server *SHOULD* send the same header fields for a HEAD as it would have for a
GET, *but* "MAY omit header fields for which a value is determined only while generating the
content." So:

- `Content-Length: 0` on a HEAD to a 547-byte resource is a SHOULD violation and, worse, actively
  misinforms caches and clients that the resource is empty.
- After the fix, the HEAD reports `547` (the static template) while the equivalent GET returns a
  prerendered body of a different, generated length with no `Content-Length` at all
  (`ServePrerenderResult` does `Response.Clear()` then `WriteAsync`, i.e. chunked). That mismatch is
  explicitly permitted by the "determined only while generating the content" carve-out, and it is
  the same answer any SSR framework gives. Reporting a plausible static length is strictly better
  than reporting zero.

### 2.4 What happens to the test that pins the reconciliation

`AbortedRequestTests.Reconciles_a_declared_content_length_with_the_empty_body_it_passes_through`
(`SpaPrerenderingMiddlewareTests.cs`) uses `AbortedStaticFile(declaredLength: 547)` — status `200`,
`text/html`, aborted token — and asserts `ContentLength == 0`.

- Under **D3 alone** it passes unchanged: the response is a body-bearing `200`, so the reconciliation
  still applies.
- Under **D1** it passes *only if the harness supplies a method*. **This is a real trap, verified:**
  `HttpRequestFeature.Method` initialises to `string.Empty`, so every harness request currently has
  `Request.Method == ""`, which is neither GET nor HEAD (`HttpMethods.IsGet("") == false` — probed
  against the real types, not recalled). A GET-only gate would therefore short-circuit **every
  existing prerendering harness test**, and this test in particular would see `ContentLength == 547`
  and fail.

**[shared] Required harness change:** default the method to GET in
`PrerenderingTestContext.Create` (`SpaPrerenderingExtensionsTests.cs:49`) — the single construction
site the middleware harness uses — so a harness request models a real request and `configureContext`
can still override it. With that in place the reconciliation test passes **untouched**, which is the
outcome to aim for: leaving it unedited makes it double as the pin proving the harness default is
present. The `Range` workstream needs the same default; whoever lands first adds it.

---

## 3. Should prerendering be gated to GET? (G5)

### 3.1 Decision: GET only

**GET+HEAD is not a coherent option.** Prerendering a HEAD cannot work even in principle: static
files writes no body for a HEAD, so the capture is empty and there is no template to render. Adding
HEAD to the gate would land us straight back on the empty-template guard, the warning noise, and the
`ContentLength` rewrite — i.e. it fixes nothing. If, instead, the intent were "render the page and
throw the body away", we would pay a full node round-trip per monitoring probe for a
`Content-Length` we then discard. Rejected on both counts.

**No gate at all** leaves three costs standing:

1. **The wasted build.** The bundle build is awaited before the capture, so a `POST` or `OPTIONS` to
   a SPA route blocks on `ng build` — first-request latency, and in the worst case a build failure
   surfaced on a request that had nothing to do with prerendering.
2. **A semantically wrong prerender.** A consumer endpoint registered *inside* the SPA callback that
   answers a `POST` with `text/html` is prerendered as if it were the SPA shell. The SPA shell is not
   the representation of a POST result.
3. **The HEAD trio above** stays alive as three separate patches instead of one gate.

### 3.2 What breaks for a consumer

Almost nothing, because non-GET requests do not work on a SPA route today either. Traced:
`StaticFileMiddleware` declines a non-GET/HEAD without touching the response, the request reaches
`SpaDefaultPageMiddleware`'s terminal `throw new InvalidOperationException(...)`
(`MintPlayer.AspNetCore.SpaServices/Internal/SpaDefaultPageMiddleware.cs:63`), and the consumer gets
a 500. Before and after the gate, identical — the gate only removes the build wait in front of it.

The single behavioural change is case 2 above: a consumer whose own middleware, registered inside
the SPA callback, answers a non-GET with `text/html` and *relies* on that being prerendered. That is
semantically wrong, undocumented, and has never been tested. Release-note it. If someone reports it,
the escape hatch is an opt-in `SpaPrerenderingOptions` member (a method set, or a
`Func<HttpContext,bool>` predicate) — **not added now** (D5): adding configuration for a
hypothetical consumer pushes the decision back onto every user and would need its own reconciliation
interaction (§2.2 point 4).

HEAD consumers are also strictly better off: pre-#79 a HEAD to a prerendered route produced
`NG05104` outright; #79 turned that into a 200 with a wrong `Content-Length` plus a warning; the gate
turns it into the plain static-file answer.

### 3.3 Placement (D2)

Current order inside the `applicationBuilder.Use(...)` delegate:

1. `Response.OnStarting(...)` registering `options.OnPrepareResponse`
2. the excluded-URL loop → `await next(); return;`
3. the bundle build await (`bootModuleBuildTask.Value`)
4. `RemoveConditionalRequestHeaders` + `Accept-Encoding` strip
5. the capture (`MemoryStream` substitution) and everything after it

**The gate goes between 2 and 3**, doing the same `await next(); return;` as the exclude loop.

Why there:

- **Before 3** is the load-bearing part: it is what stops a `POST`/`OPTIONS`/`HEAD` blocking on
  `ng build`. A gate placed after the build await would fix the HEAD headers and the wrong-prerender
  case but leave the wasted build — the PRD's own reason for raising G5.
- **After 1**, so `OnPrepareResponse` keeps firing for every method exactly as today. That callback
  is a consumer hook for setting headers on the pass-through response; silently dropping it for HEAD
  would be a second, quieter regression. (The exclude loop already sits after 1 for the same
  reason.)
- **After 2** rather than before it, for one reason only: log noise. A skip log placed before the
  exclude loop would fire for every non-GET hit on `/dist/*` and friends. Behaviourally the two
  orderings are equivalent — both terminate in `await next(); return;` — so this is a diagnostics
  choice, taken deliberately rather than by accident.

Log level for the skip: **Debug**, with `{Method}` and `{Path}`. It is a normal, expected outcome,
not an anomaly.

---

## 4. The HEAD warning noise (D4)

Today the empty-template guard logs at **Warning** for *every* HEAD to a SPA route, with the comment
*"There is no known benign cause, so this is worth a warning."* A HEAD is a benign cause, so the
comment is false as written, and every uptime probe and link checker produces a warning.

**With the gate, the HEAD path never reaches the guard** — the gate returns at step 2/3, long before
the capture is installed, so no HEAD can produce an empty capture. Confirmed by construction, and
pinned by a test (§5, test 3).

The comment should stop asserting the absence of benign causes and instead say which ones were
*removed* and what remains:

> An empty template is never something the prerenderer can work with, and handing it to Node is what
> produces an unhelpful NG05104 instead of a diagnosable message. The one benign producer we know of
> — a HEAD, which static files answers with headers and no body — is short-circuited by the method
> gate above and cannot reach here, so an empty capture on a GET means the body was never written
> when it should have been. That is worth a warning.

Two supporting changes:

- **Add `{Method}` and `{StatusCode}` to the message.** The reporter in #80 had to hand-build this
  logging (PRD candidate 5). If a benign producer we have not thought of turns up, the log line
  should identify it in one reading instead of provoking a second investigation.
- **[shared] Optional, and it touches the other workstream's guard:** a `204`/`205` on a GET reaching
  the empty guard is *also* benign (an empty body is correct there), and would still warn. The
  cleanest form is to reuse the same `CanHaveResponseBody` predicate from §2.2 to log at Debug in
  that case and Warning otherwise. Recommended but explicitly flagged: the guard body is the
  `Range`/206 agent's file region, so reconcile at merge rather than editing it twice.

---

## 5. Is a prerendered body ever written to a HEAD response today? (Q4)

**No body reaches the wire today, and the change does not weaken that — it strengthens it.** Verified
against the code rather than taken from the PRD:

- **The normal HEAD path never renders.** static files → `200`/`text/html`/full `Content-Length`/no
  body → empty capture → the empty guard returns via `PassThroughAsync`, which copies zero bytes.
  `Prerenderer.RenderToString` is never reached, so there is no prerendered body to write.
- **The PRD's claim needs one correction, in our favour.** "Kestrel silently discards HEAD body
  writes anyway" is true of the *real* response body, but **not** of the capture: while our
  `MemoryStream` is installed, `Kestrel`'s non-body-response handling is bypassed entirely, so a
  consumer middleware inside the callback that *does* write a body on a HEAD would fill our buffer,
  clear the empty guard, and get prerendered. What then happens is that `ServePrerenderResult` calls
  `Response.Clear()` (wiping status-relevant headers and `Content-Length`), sets
  `Content-Type: text/html`, possibly overwrites the status from `renderResult.StatusCode`, and
  `WriteAsync`es the page into the *real* body — where Kestrel discards it, and
  `VerifyResponseContentLength` skips HEAD so nothing complains. So today's HEAD protection is "no
  body on the wire, but the response headers can still be rewritten by a render that was never
  appropriate", and the render itself (a full node round-trip) still happens.
- **Under the gate that entire path becomes unreachable for HEAD**, including the header rewrite and
  the wasted render. The protocol-violation surface strictly shrinks; nothing about it grows.

`204`/`205` with a body written inside the capture remains a real hazard (`Kestrel`'s
`HandleNonBodyResponseWrite` would throw on the pass-through write) — unchanged by this work, and
noted in the PRD's Tier C. The §2.2 narrowing does not make it worse: it only declines to *mutate*
the declared length.

---

## 6. Tests (committed, node-free)

All against `PrerenderingHarness` in
`MintPlayer.AspNetCore.SpaServices.Tests/Prerendering/SpaPrerenderingMiddlewareTests.cs`. There is
no HEAD coverage anywhere in the prerendering tests today, which is precisely why this shipped.

### 6.1 Harness additions

1. **[shared] `PrerenderingTestContext.Create` defaults `Method` to `HttpMethods.Get`.** Mandatory —
   see §2.4. Without it a GET-only gate fails the whole existing suite.
2. **`PrerenderingHarness.StaticFileHead(long declaredLength)`** — a new inner pipeline modelling
   what `StaticFileMiddleware` leaves behind for a HEAD: `200`, `text/html`,
   `ContentLength = declaredLength`, **no body**, no exception. Deliberately distinct from
   `AbortedStaticFile`, which is byte-identical in effect but models a different contract; two names
   keep the two contracts from being conflated when one of them changes.
3. **A collecting logger.** The harness registers no `ILoggerFactory`, so `LoggerFinder` currently
   hands the middleware `NullLogger.Instance` and log assertions are impossible. Add a small
   in-harness `ILoggerProvider`/`ILogger` recording `(LogLevel, string)` pairs and a
   `Run(..., collectLogs: true)`-style parameter that registers it in the `ServiceCollection`
   **before** `UseSpaPrerendering` (the logger is resolved at registration time, not per request).
   No new package: `ILogger`, `ILoggerFactory` and `LoggerFactory` all come from the existing
   `Microsoft.AspNetCore.App` framework reference — do **not** pull in
   `Microsoft.Extensions.Diagnostics.Testing`/`FakeLogger` for this.
4. **`RecordingBootModuleBuilder : ISpaPrerendererBuilder`** with a `BuildCount`, plus a way to pass
   `options.BootModuleBuilder` through `Run` (a `configureOptions` parameter). Returns a completed
   task — asserting on a count is a better failure mode than a never-completing task, which would
   hang the run instead of failing it.

### 6.2 New class `RequestMethodGateTests`

| Test | Asserts | Fails today? |
|---|---|---|
| `A_head_request_keeps_the_full_content_length_it_was_given` | HEAD + `StaticFileHead(547)` → `Response.ContentLength == 547`, client body empty. | **Yes** — currently 0. This is the shipped regression, pinned. |
| `A_head_request_does_not_reach_the_prerenderer` | HEAD + `HtmlPage(index)` (a pipeline that *does* write a body, so the empty-template guard cannot be what saves us) → `Service.WasCalled == false`, `ExplodingNodeServices` never throws, client body byte-identical to the input. | **Yes** — today the service is called. This is the test that pins the *gate* rather than the guard. |
| `A_head_request_does_not_log_a_warning` | HEAD + `StaticFileHead(547)` with the collecting logger → no entry at `LogLevel.Warning`; exactly one `Debug` entry naming the method. | **Yes** — today one Warning per HEAD. |
| `A_head_request_does_not_wait_for_the_bootmodule_build` | HEAD + `RecordingBootModuleBuilder` → `BuildCount == 0`. | **Yes** |
| `A_post_request_is_passed_through_without_prerendering` | POST + `HtmlPage(index)` → `WasCalled == false`, body and `ContentLength` unchanged. | **Yes** |
| `An_options_request_does_not_wait_for_the_bootmodule_build` | OPTIONS + `RecordingBootModuleBuilder` → `BuildCount == 0`. | **Yes** — the PRD's stated G5 cost. |
| `A_get_request_is_still_prerendered` | Control: default (GET) + `HtmlPage` → `WasCalled == true`, `BuildCount == 1`. Guards against an inverted gate and against the harness default being lost. | No (passes today too — that is the point) |
| `Does_not_rewrite_the_content_length_of_a_bodyless_response` — `[Theory]` over `204`, `205`, `304` | GET, `ContentLength` declared, no body → `ContentLength` preserved. | **Yes** — currently rewritten to 0. Covers §2.2's D3 on a path the gate cannot reach. |

### 6.3 Existing tests

- `Reconciles_a_declared_content_length_with_the_empty_body_it_passes_through` — **left untouched and
  still passing** (body-bearing `200`, GET via the harness default). It keeps its role as the pin for
  the synthetic-token path and gains a second one: if the harness's GET default is ever lost, it
  fails.
- Every other test in `OriginalHtmlCaptureTests` / `AbortedRequestTests` /
  `PrerenderCancellationTests` — unchanged, and passing *because* of harness addition 1. Worth
  stating in the PR body: their green status depends on that one line.

---

## 7. Release impact (D6)

**Recommendation: supersede with 10.7.1. Do not pull 10.7.0.**

Severity, proportionately:

- **Who is affected:** only clients issuing a `HEAD` to a prerendered SPA route on 10.7.0. In
  practice: uptime and health probes, link checkers, some CDN/reverse-proxy revalidation, crawlers.
  Browsers do not `HEAD` a document navigation.
- **How badly:** the HEAD reports `Content-Length: 0` with a `200` and `text/html`. A probe checking
  for a 200 still passes. A checker that asserts a non-zero length, or a cache that stores a
  zero-length entry for the route, misbehaves. **No GET is affected**, so no user-facing page is
  broken by it — and #79's guard simultaneously *fixed* a live HEAD defect that had been producing an
  outright `NG05104` (a 500) for every HEAD before it. Net, HEAD handling on 10.7.0 is better than
  on 10.6.x, just still wrong.
- **Contrast with #80's `Range` defect**, which is in the same release train: that one produces real
  500s on GETs, several times per hour, in the reporter's production. It is the reason for the
  release; the HEAD item rides along.

Pulling 10.7.0 would strand the abort/decode fixes it carries (a strictly larger user-facing win)
and break restores for anyone already pinned, to remedy a wrong header on a method browsers do not
use. Not proportionate.

Suggested PR-body wording:

> Also fixes a regression introduced in 10.7.0 (#79): `PassThroughAsync` reconciled `Content-Length`
> unconditionally, so a `HEAD` to a prerendered SPA route answered `200 text/html` with
> `Content-Length: 0` instead of the template's real length — a HEAD must report what the equivalent
> GET would (RFC 9110 §9.3.2), and Kestrel does not catch it because `VerifyResponseContentLength`
> skips HEAD. Prerendering is now gated to `GET`, which fixes it at the source: a `HEAD` (or `POST`,
> or `OPTIONS`) no longer enters the capture, no longer blocks on the SSR bundle build, and no longer
> logs a warning per request. The reconciliation itself is additionally narrowed to responses that
> can carry a body, so a `204`/`205`/`304` keeps its declared length.
>
> HEAD-only impact, GETs unaffected; 10.7.0 does not need pulling, 10.7.1 supersedes it. Behaviour
> change to release-note: a consumer middleware registered inside the SPA callback that answered a
> non-GET with `text/html` was previously prerendered and now is not.

Also update, in the same PR (one PR, per the repo's convention): the Prerendering README's
prerendering-behaviour section, to state that prerendering applies to `GET` only and what a `HEAD`
now returns.

---

## 8. Open points for the maintainer

1. **[shared] The harness GET default** is the one edit both workstreams need in the same region;
   land it once.
2. **[shared] The empty-guard `204`/`205` Debug refinement** (§4) sits in the other workstream's file
   region — take it or leave it, but decide once.
3. **`TRACE`/`OPTIONS` niceties** are out of scope: the gate passes them through, and what happens
   next (the `SpaDefaultPageMiddleware` throw) is pre-existing behaviour this PR does not change.
