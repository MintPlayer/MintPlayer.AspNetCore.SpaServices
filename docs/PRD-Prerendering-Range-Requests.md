# PRD: `originalHtml` is a one-byte slice on a `Range` request

Upstream report: [issue #80](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/80)
by @Reonekot, filed after deploying [PR #79](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/79)
to production.

## Overview

A request carrying a `Range` header makes `StaticFileMiddleware` serve a **slice** of `index.html`
with **HTTP 206**. `UseSpaPrerendering` accepts that slice as a complete SSR template, so
`originalHtml` becomes `"<"` (for `bytes=0-0`) and Angular throws `NG05104`. The reporter sees it
"a few times per hour" in production.

**Reproduced, deterministically.** Both in a unit test and end to end against `Demo.Web`. This is
the third report of the same *shape* — the middleware's notion of "this capture is a usable SSR
template" being too weak — so this document is deliberately written against the class, not only
against `bytes=0-0`.

## Problem statement

Three independent conditions have to hold, and all three do
([`SpaPrerenderingExtensions.cs`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs)):

1. **`Range` is not stripped.** `RemoveConditionalRequestHeaders` (line 341) removes `If-Match`,
   `If-Modified-Since`, `If-None-Match`, `If-Unmodified-Since` and `If-Range` — but **not**
   `HeaderNames.Range`.
2. **206 counts as success.** `IsSuccessStatusCode` (line 338) is `>= 200 && < 300`, so
   `206 Partial Content` satisfies `canPrerender`, and a 206 *does* carry the file's own
   `Content-Type: text/html` (verified in source, below).
3. **The empty-template guard does not fire.** `string.IsNullOrWhiteSpace("<")` is `false`.

### Confirmed mechanism

Pinned to `dotnet/aspnetcore` `release/10.0` @ `668a5ab288b317029bba5ebfe87a7ea6347ea450`.

`ComprehendRequestHeaders` → `ComputeRange` parses `Range` for every GET;
`ServeStaticFile` then takes `if (IsRangeRequest) { await SendRangeAsync(); return; }`, which sets
`Content-Range`, sets `Content-Length` to the *slice* length, and calls
`ApplyResponseHeadersAsync(206)`. That method sets `Content-Type` for any status `< 400` — the
comment in the framework source says so explicitly: *"these headers are returned for 200, 206, and
304"*. So we capture `206 + text/html + Content-Range` with a body of exactly `count` bytes.

The slicing survives our `Response.Body` substitution: `StaticFileContext` computes the offset and
count and passes them *down* as arguments, and `SendFileFallback` does
`fileStream.Seek(offset)` + copy `count` bytes. **There is no timing window** — unlike the abort
defect in the previous PRD, this is fully deterministic, which matches the reporter's "logs
consistently".

### Three corrections to the report

1. **The trigger is far broader than `bytes=0-0`.** *Any satisfiable single range* produces a 206:
   `bytes=-1` (last byte), `bytes=10-20`, and even `bytes=0-` (the whole file, under a 206). And
   because neither `RangeHelper.ParseRange` nor `RangeHeaderValue` validates the range **unit**,
   `Range: items=0-0` works too. **A fix keyed on `bytes=0-0`, or on the `"<"` character, would be
   incomplete.**
2. **Our `If-Range` strip is part of the cause, not a bystander.** `ComputeIfRange` is the *only*
   code path that can cancel an already-parsed range (it clears `IsRangeRequest` when the validator
   does not match). By removing `If-Range` we guarantee it never cancels — so our own header surgery
   makes the range *more* likely to be honoured than it would otherwise be.
3. **Unsatisfiable and multi-range requests are already safe**, for two independent reasons each:
   an unsatisfiable range yields **416**, which fails `IsSuccessStatusCode` *and* gets no
   `Content-Type` at all (`ApplyResponseHeadersAsync` sets it only for `< 400`); a multi-range or
   malformed header is *ignored* by ASP.NET Core, which serves a full 200. Neither needs fixing.

### Measured reproduction

Unit: 7 cases in `Tests/Prerendering/RangeReproTests.cs` (scratch — the committed suite is M3's job).

End to end, `Demo.Web`, `GET /person`, **Production**, instrumented `OnSupplyData`:

| `Range` | Status | `Content-Range` | Template reaching the prerenderer | Client sees |
|---|---|---|---|---|
| *(none)* | 200 | – | 456 chars, full document | 200, 25252 B prerendered |
| `bytes=0-0` | **206** | `bytes 0-0/547` | **1 char: `<`** | **500** (NG05104) |
| `bytes=0-99` | **206** | `bytes 0-99/547` | 100 chars — *and it starts with `<!doctype html><html lang="en">`* | **500** |
| `bytes=100-200` | **206** | `bytes 100-200/547` | 101 chars of mid-document, no `<html` at all | **500** |
| `bytes=999999-` | 416 | `bytes */547` | *(never reaches it)* | 416 |
| `bytes=0-0,2-2` | 200 | – | 456 chars, full document | 200 prerendered |

Deterministic: 7/7 identical across repeats. The failure chain is
`NG05104` → `NodeInvocationException` from `SpaPrerenderingExtensions.cs:239` → 500, and the 206's
`Content-Range` / `Content-Length: 1` **leak onto the error response**.

**The `bytes=0-99` row is the important one for design.** That fragment starts with a doctype and an
`<html` tag, so any guard that merely asks "does this look like HTML?" would pass it. Whatever
replaces the current check must be about *completeness*, not plausibility.

### Environment

**Production-only in practice — but by accident, not by design.**

- Development: all six variants returned 200, chunked, no `Content-Range`, full 362-char template.
  The Angular 21 / Vite dev server does not implement `Range` for its index document.
- But our own `SpaProxy` **forwards `Range`** (`NotForwardedHttpHeaders` contains only
  `Connection`), and `CopyProxyHttpResponse` copies both the status and the `Content-Type`. So a
  dev server that *does* answer 206 for its index document would reproduce this in development
  identically. **The fix must be proxy-agnostic, not a static-files special case.**
- Chromium sends **no** `Range` header on a document navigation or reload (verified with Playwright).
  So this is bot, scanner, CDN or reverse-proxy traffic — consistent with the reporter's guess, and
  it means user-facing impact is limited to whatever those clients do with a 500.

## ⚠ A shipped regression from PR #79, found by this investigation

**#79 is merged (`29db888`), so this is live on `master`.** Not part of #80, and it is ours:

**`PassThroughAsync` rewrites a `HEAD` response's `Content-Length` to 0.** Verified with a throwaway
test: expected 547, actual **0**.

`StaticFileMiddleware` answers a HEAD with `SendStatusAsync(200)` — `Content-Type` set,
`Content-Length` set to the **full** file length, and no body at all. Our empty-template guard
catches the empty capture and passes through, and then #79's `ContentLength` reconciliation sees
`547 != 0` and "corrects" the length to 0. A HEAD must report the length the equivalent GET would
return, so this is a protocol violation introduced by the commit that fixed the synthetic-token
mismatch.

Second, milder problem on the same path, also from #79: the empty-template guard logs at
**Warning** for every HEAD, and its code comment claims *"There is no known benign cause, so this is
worth a warning."* A HEAD is a perfectly benign cause. Every monitoring probe and link checker now
produces a warning.

Worth noting the silver lining: before #79, a HEAD to a prerendered route produced `NG05104`
outright. #79's guard accidentally fixed a live bug nobody had reported — the same class again.

## The class, enumerated

`canPrerender` asks two questions — is the status 2xx, is the media type `text/html` — and infers
"this capture is the complete document the client would have received." **Neither question is about
completeness.** Every finding below makes both answers true while the inference is false.

### What NG05104 actually means

Traced through the render chain: `prerenderer.js` → the app's `createServerRenderer` →
`renderApplication({ document })` → `parseDocument` → domino `createWindow`. **Domino never rejects
input** — any byte sequence becomes *a* document, so there is no syntax-error failure mode at all.
The single requirement is one line in `_dom_renderer-chunk.mjs`:

```js
let el = typeof selectorOrNode === 'string' ? this.doc.querySelector(selectorOrNode) : selectorOrNode;
if (!el) { throw new _RuntimeError(-5104, `The selector "${selectorOrNode}" did not match any elements`); }
```

So **NG05104 means exactly one thing: the parsed template contained no element matching the app's
root selector.** That is why "syntactically fine but unusable" is the right framing, and why a
plausibility check cannot work.

### Tier A — confirmed reachable in a normal deployment

| # | Finding |
|---|---|
| **A1** | **`HEAD` → 200 + `text/html` + full `Content-Length` + zero bytes.** Caught by the empty-template guard, but as a *false alarm*: every HEAD to a SPA route logs at **Warning**, indistinguishable from a real fault. Plus the #79 `Content-Length` regression above. No HEAD coverage exists anywhere in the prerendering tests. |
| **A2** | **206.** Three additions to the mechanism above: a 206 can carry the *complete* document (`bytes=0-`), so "206 ⇒ truncated" is false — rejecting it is still right, because `Content-Range` framing cannot survive a rewritten body. Other producers reachable inside the capture: an MVC `FileResult` with `enableRangeProcessing: true`, and `MapStaticAssets` if ever positioned inside. |
| **A3** | **The completeness signal already exists and is used backwards.** `PassThroughAsync` computes `ContentLength.HasValue && ContentLength != outputBuffer.Length` — exactly the check the middleware lacks — and *overwrites the declared value* instead of concluding "this capture is not the document." It is also the only check that can catch the abort case (b) mid-copy truncation that the empty guard provably cannot. |
| **A4** | Nothing on the response is inspected except status class and media type. Never read: `Content-Range`, `Content-Encoding`, `Transfer-Encoding`, `Request.Method`, or A3's length comparison. |
| **A5** | Deferred status codes still defeat the post-`OnSupplyData` check — `SkipPrerendering()` is opt-in, and the demo's own service sets a 404 from inside `OnStarting`. |

### Tier B — needs an unusual but realistic configuration

| # | Finding |
|---|---|
| **B1** | **`Content-Encoding`.** The `Accept-Encoding` strip is a precaution with no check behind it. No currently-reachable compressed capture found (`ResponseCompressionMiddleware` is double-gated; `StaticFileMiddleware` does *no* pre-compressed on-disk negotiation — that hypothesis is retired). But `MapStaticAssets` serves Brotli variants as separate endpoints and copies `Content-Encoding` verbatim, reachable when endpoint *selection* is upstream of our strip and *execution* downstream. Compressed bytes then get `Encoding.UTF8.GetString`-ed. |
| **B2** | **Non-UTF-8 charset.** `IsHtmlContentType` accepts any parameters; the decode hard-codes UTF-8. `text/html; charset=utf-16` decodes to `<\0!\0d\0…` — and `char.IsWhiteSpace('\0')` is **false**, so it sails past the empty guard into NG05104. Needs a custom content-type provider, so unusual rather than theoretical. |
| **B3** | **A downstream error or status page becomes the template — the most misleading case of all.** `UseExceptionHandler` / `UseStatusCodePagesWithReExecute` registered *inside* the SPA callback puts the error page inside the capture (the demo's own commented-out `spa.ApplicationBuilder.UseResponseCaching()` invites exactly that placement). `ExceptionHandlerOptions.StatusCodeSelector` is an unconstrained `Func<Exception,int>`, and a re-executed MVC `Ok()` turns a 404 into a 200. Result: 200 + `text/html` + a complete, well-formed, non-empty error page with no app root → NG05104, from a template that passes every check and **looks perfectly healthy in a log**. |
| **B4** | **`Response.Clear()` while our `MemoryStream` is installed is reachable — and this corrects the previous PRD.** `ResponseExtensions.Clear` does `if (response.Body.CanSeek) response.Body.SetLength(0)`; our buffer is seekable and `HasStarted` is untouched, so it neither throws nor no-ops — it *shrinks our buffer*. Both `ExceptionHandlerMiddlewareImpl` and `DeveloperExceptionPageMiddlewareImpl` reach it. The consequence is nil **only because** the decode moved to `TryGetBuffer`; the pre-#79 `GetBuffer()` would have appended the stale tail of the discarded page. Also: `Clear()` wipes all headers, so a downstream `Clear()` that forgets `Content-Type` silently turns prerendering off. |
| **B5** | **Buffered writes that never reach the buffer.** `StreamResponseBodyFeature.Writer` is a `PipeWriter` whose bytes reach the stream only on flush, and its `StartAsync` flushes the *stream*, not the writer. We read the buffer immediately after `next()` and never call the feature's `CompleteAsync`. No current instance found (`WriteAsync`, MVC and Razor all flush explicitly), but a custom middleware or third-party body wrapper would hand us a short capture with no abort involved. |

### Tier C — theoretical or unreachable here

203 (no producer in ASP.NET Core or this repo), 226 (nothing implements delta encoding), 204/205
(must have no body; benign *only because* the empty guard fires — with a body present, Kestrel's
`HandleNonBodyResponseWrite` would throw).

**Non-GET methods carry no corrupt-template route** — `StaticFileMiddleware` declines without
touching the response and the request falls to `SpaDefaultPageMiddleware`'s terminal throw. But they
do cost something: the SSR bundle build is awaited **first**, so a `POST` or `OPTIONS` to a SPA route
blocks on `ng build`. Prerendering is meaningful only for GET.

## Goals

| # | Goal |
|---|---|
| G1 | A `Range` request never yields a partial SSR template. Covered by tests that fail on the current branch. |
| G2 | Fix the *class*: a status that does not promise the complete representation must never be treated as a template, whatever produced it (static files, dev proxy, or a consumer's middleware). |
| G3 | Replace or supplement the `IsNullOrWhiteSpace` guard so a short-but-non-empty capture is rejected — without inventing a check that a legitimate template could fail. |
| G4 | Fix the #79 HEAD `Content-Length` regression, and stop warning on benign HEADs. |
| G5 | Decide whether prerendering should apply to non-GET methods at all. |
| G6 | Land it with the rest of the work (see the open question on PR placement). |

## Non-goals

- Honouring `Range` *correctly* for prerendered output. A prerendered page has no stable byte range
  to slice — the whole point is that the body is generated per request. Ignoring `Range` is both
  permitted (a server MAY ignore it) and the only coherent option.
- Reproducing against IIS or HTTP.sys. Kestrel only, as before.

## Candidate fixes (the solution phase decides)

Stated as options with their trade-offs, not as a decision.

1. **Strip `Range` alongside the conditional headers.** Directly consistent with what that method
   already does and why — we strip `If-None-Match` precisely so we do not capture a 304. Making it
   strip `Range` too means static files serves the full 200 we actually want. Note this also
   *repairs* correction 2: with both `Range` and `If-Range` gone, there is nothing to cancel.
   Open question: restore it afterwards, as we do for `Accept-Encoding`? Nothing downstream of the
   capture reads it, but symmetry may be worth it.
2. **Narrow the status check.** `IsSuccessStatusCode` is used for `canPrerender`; a template must be
   a complete representation, which realistically means **200 only**. Alternatively reject on the
   presence of a `Content-Range` response header, which is set on the 206 and 416 paths and never on
   the 200 path. Defence in depth against a 206 arriving from the dev proxy or a consumer's
   middleware even after (1).
3. **A completeness check on the captured template — in layers, from the breadth survey.** Strongest
   per unit of cost first, and note the split between *invariants* and *heuristics*:
   1. **Framing invariants that prove "this is the whole document"**: status exactly 200 (not 206),
      no `Content-Range`, `Content-Encoding` absent or `identity`, method is GET, and — if
      `ContentLength` was declared — captured bytes == declared bytes. All app-agnostic, all
      decisive, and together they cover A1, A2, B1 *and* the abort case (b) truncation the empty
      guard provably cannot. **A3 is the point: that last comparison is already computed in
      `PassThroughAsync` and thrown away.**
   2. **Decode integrity**: reject a capture that decodes to U+FFFD or contains U+0000 (equivalently,
      decode with `throwOnInvalidBytes: true`). Neither can occur in a valid template, and both are
      exactly what B1's compressed bytes and B2's wrong charset produce. This is the only check that
      catches those two without knowing anything about the app.
   3. **Structural HTML as a logged *warning*, not a rejection**: presence of `<html` and `</html>`.
      Honest about the limit — a fragment template is legitimate (domino normalizes a bare
      `<app-root></app-root>` into a full document and the render succeeds), so a hard rejection
      would break a working deployment for no proven gain. True in practice for any `ng build`
      output, false in principle. Log it with the first ~200 characters of the template — precisely
      the diagnostic the reporter had to hand-build.
   4. **Not worth asserting at all**: any guess at the root element. `<app-` prefix matching,
      "contains a hyphenated custom element", "has a child of `<body>`" all fail on real apps and
      would turn working deployments into 500s. `Contains("<our-app")` is correct *for the reporter*
      and correctly lives in *their* `OnSupplyData`. If an in-library strong check is wanted, the
      only sound route is opt-in: a configured root selector or a template predicate on
      `SpaPrerenderingOptions`.

   Note from the measurements that a leading-doctype check would **not** have caught `bytes=0-99`,
   which is why layer 3 is a warning and layers 1-2 do the actual work.
4. **A method gate.** Prerendering only makes sense for GET. A gate would fix the HEAD path cleanly
   at the source — no capture, no warning, no `ContentLength` rewrite — and would stop a POST that
   happens to return `text/html` from being prerendered. Needs a judgement on whether any consumer
   legitimately relies on prerendering a non-GET.
5. **Log enough to identify the client.** The reporter had to add their own logging to get this far.
   A rejected-template log line carrying the method, status, `Content-Range` and User-Agent would
   have identified this in one reading.

## Risks

| Risk | Mitigation |
|---|---|
| Stripping `Range` means a client asking for bytes gets a full page | Correct and permitted; a prerendered body cannot satisfy a byte range. Same precedent as the conditional headers we already strip. Call it out in the release notes. |
| A completeness check rejects a legitimate template and silently disables prerendering | The measured slices are the test set, and the previous PRD's rejection of the `ContentLength` heuristic is the precedent: a false positive here is worse than the bug. Prefer status/header-based rejection over content sniffing. |
| A method gate changes behaviour for a consumer prerendering a non-GET | Assess first; if adopted, release-note it. GET-only matches what `StaticFileMiddleware` itself serves. |
| Fixing the HEAD regression inside #79 rewrites an open PR under review | It is a protocol violation we introduced; shipping it knowingly is worse. Raise with the maintainer (open question below). |

## Success criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Tests that fail before the fix and pass after, covering `bytes=0-0`, the markup-shaped slice, the mid-document slice, and 416 as a control | ✅ **Verified in both directions.** Reverting the `Range` strip, the GET gate, the framing checks and the bodyless narrowing turns **25 tests red**; restoring them gives **362 green**. |
| 2 | The `Range` end-to-end matrix re-run against the fix — every variant prerenders the full page or passes through, no 500, no `NG05104` | ✅ **All seven variants → 200, 25252 B, fully prerendered.** No 206 reaches the client, no `Content-Range` header anywhere, nothing logged. |
| 3 | HEAD reports the full `Content-Length`, does not warn, does not reach the prerenderer | ✅ Unit (`RequestMethodGateTests`) **and** end to end: `curl -I /person` → `Content-Length: 547` (was `0`), one Debug line naming the method, zero Warnings. |
| 4 | No regression in the 400-abort verification from the previous PRD | ✅ 200 real Chromium aborts: **0 NG05104, 0 5xx, 0 empty-template prerenders**, 195 complete templates, 6 abort-skip Debug lines. |

### End-to-end results (Production, `Demo.Web`)

| `Range` | Before | After |
|---|---|---|
| *(none)* | 200, 25252 B | 200, 25252 B |
| `bytes=0-0` | **206 → 500 NG05104** | **200, 25252 B, prerendered** |
| `bytes=0-99` | **206 → 500** | **200, 25252 B** |
| `bytes=100-200` | **206 → 500** | **200, 25252 B** |
| `bytes=999999-` | 416 | **200, 25252 B** — see the behaviour note below |
| `bytes=0-0,2-2` | 200, 25252 B | 200, 25252 B |
| `bytes=-1` | *(untested)* | 200, 25252 B |

Baseline unaffected: `/person` still returns 25252 bytes of real prerendered markup, 0.03-0.06 s warm.
`GET /` still redirects with a 456-byte pass-through rather than a prerendered page.

**Behaviour change for the release notes:** `bytes=999999-` no longer returns **416**, it returns a
full 200. The header is gone before `StaticFileMiddleware` sees it, so unsatisfiability can no longer
be detected. Consistent with the non-goal above (a server MAY ignore `Range`, and a prerendered body
has no stable byte range to slice), but it is a visible difference.

### Two findings from the verification run

1. **The structural warning was a false positive under an HTML minifier — fixed.** The check
   required both `<html` and `</html>`, but the end tags for `html` and `body` are optional, so
   aggressive minification legitimately strips them. The demo runs `UseWebMarkupMin` inside the SPA
   callback, i.e. downstream of the capture, so its perfectly good 456-character template was
   reported as having "no `<html>` element". The check now requires only the opening tag, which is
   not optional and not stripped. Pinned by
   `Does_not_warn_about_a_minified_template_whose_closing_tags_were_removed`.
2. The synthetic abort probe still 500s with a `Content-Length` mismatch, from the minifier's
   declared length rather than from this middleware. Unreachable on a genuine socket abort — 0
   occurrences across 200 real aborts — and already documented in `SOLUTION-defect2-abort.md`.

**Not reachable in this demo:** the gate's non-GET Debug line for `POST`. Endpoint routing answers
`405 Allow: GET, HEAD` *upstream* of `UseSpaPrerendering`, so only `HEAD` exercises that branch here.
The behaviour is right; the log line is simply unobservable in this app.

### Coverage of the enumerated class

| Finding | Closed? |
|---|---|
| A1 HEAD | ✅ GET gate; `Content-Length` regression fixed |
| A2 206 / partial | ✅ `Range` stripped, status must be exactly 200, `Content-Range` rejected |
| A3 completeness signal discarded | ✅ now a rejection (F4), narrowed and logged |
| A4 nothing inspected but status class and media type | ✅ method, `Content-Range`, `Content-Encoding` and length are all read now |
| A5 deferred status codes | ➖ unchanged — `SkipPrerendering()` remains opt-in, as designed |
| B1 `Content-Encoding` | ✅ F3 |
| B2 non-UTF-8 charset | ✅ `Utf8.IsValid` + the NUL check |
| B3 error page as template | ❌ **Not closed, by construction.** A complete, well-formed error page passes every check, including the structural one. Documented instead: the README warns against registering `UseExceptionHandler`/`UseStatusCodePagesWithReExecute` inside the SPA callback, and shows the consumer-side assertion in `OnSupplyData`. The eventual answer is an opt-in template predicate on `SpaPrerenderingOptions`. |
| B4 `Response.Clear()` shrinking the buffer | ➖ Incidental — harmless since the decode moved to `TryGetBuffer`. The previous PRD's claim that it was unreachable is corrected there. |
| B5 unflushed writer | ✅ when a length is declared (F4); otherwise the empty guard |
| Tier C (203/204/205/226) | ✅ rejected by the exactly-200 check; 204/205/304 report at Debug, since an empty body is correct for them |

## Placement — resolved

PR #79 was merged as `29db888` before this investigation finished, so the question of folding into
it is moot. This work goes on `bugfix/prerendering-range-requests`, branched from the merged
`master`, as one new PR covering issue #80 **and** the shipped HEAD regression.

That changes the HEAD item's urgency rather than its content: it is no longer "do not ship this",
it is "this shipped in 10.7.0 and needs a fix release". Worth a line in the PR body so the
maintainer can decide whether 10.7.0 needs pulling or simply superseding.
