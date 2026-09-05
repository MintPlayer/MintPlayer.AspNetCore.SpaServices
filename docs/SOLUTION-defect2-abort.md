# Solution: Defect 2 (abort check) + G5 (unusable-template guard)

Milestone **M4** decision document for
[PRD-Prerendering-Aborted-Requests.md](./PRD-Prerendering-Aborted-Requests.md) /
[PLAN-Prerendering-Aborted-Requests.md](./PLAN-Prerendering-Aborted-Requests.md).

Scope of this document: the abort early-return, the G5 empty/unusable-template guard, logging, the
line-161 bypass (collateral finding #1), and the committed tests for all of the above — all in
`MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs`.

**Out of scope here** (owned by other M4 agents, deliberately not touched): the `GetBuffer()` length
/ BOM decode at line 149 (Defect 1 / 1b), and Workstream 3's cancellation-token threading.
Where a decision below interacts with theirs, the interaction is called out rather than resolved.

No production code is written by this document. M5 implements.

---

## 1. The abort check — **Spike 6's recommendation is CONFIRMED**

```csharp
// immediately after the finally block, before canPrerender is computed
if (context.RequestAborted.IsCancellationRequested)
{
    await outputBuffer.CopyToAsync(context.Response.Body);
    return;
}
```

### Why confirmed rather than challenged

Four properties were checked against the code as it stands, and all four hold.

1. **Copy-then-return is never worse than a bare `return`, and sometimes better.** The PRD's three
   reachable outcomes decide the content of `outputBuffer`:
   - case (a), 0 bytes → `MemoryStream.CopyToAsync` short-circuits at `if (n == 0)` and touches
     `Response.Body` not at all. Byte-for-byte identical to `return;`, so the copy costs nothing.
   - case (b), partial bytes → a bare `return` discards bytes the downstream pipeline already
     produced; the copy forwards them. Both are irrelevant to a departed client, but the copy is the
     one that does not lose data if the abort signal turns out to be spurious.
   - case (c), complete body → this is the interesting one. `return;` throws away **the entire,
     correct page**. The copy delivers it. Since case (c) is not a narrow window for any
     `index.html` under 16 KiB (one read, one write), a bare `return` would convert a
     harmlessly-late abort into a guaranteed empty response.
   There is no outcome in which the bare `return` wins.

2. **It preserves the file's single exit convention.** Lines 140 and 163 are both
   `copy → return`. A third exit that returns without copying would make a reader compare the three
   and guess which is the bug. Uniformity here is worth more than the two saved lines.

3. **Placement before `canPrerender` is right.** The two conditions answer different questions —
   `canPrerender` asks "is this a usable HTML template?", the abort asks "should we be doing any of
   this work at all?" — and folding the abort into `canPrerender` would also skip the abort check on
   the `!IsSuccessStatusCode` path, which is precisely a path an aborted request can take. Keeping
   it separate also means the check is stated once, ahead of everything, which is how it reads.

4. **`RequestAborted` is the right signal, and an empty-buffer test is not a substitute.** Confirmed
   by the PRD: an abort mid-copy (case (b), and via the dev proxy's mid-copy window) leaves a
   non-empty truncated template. Only the abort check covers it. The reverse also holds — an empty
   buffer with no abort is reachable (some third path nobody has found), so the two guards each
   cover something the other does not. This retires the plan's overlap question in the affirmative:
   **both guards ship.**

### The token constraint, restated and locked in by a test

Do **not** pass `context.RequestAborted` (or any linked token) into *this* `CopyToAsync`.
`MemoryStream.CopyToAsync` front-checks the token and returns `Task.FromCanceled`, so on the abort
path — where the token is by definition already cancelled — the middleware would throw an
`OperationCanceledException` out of the pipeline on **every** aborted request, including case (a)
where there is nothing to copy. That converts a silent no-op into an exception and, per collateral
finding #2, feeds `UseExceptionHandler` a request whose token is already dead.

This is the one place where Workstream 3's "thread the token everywhere" instinct is wrong. It is
also easy to reintroduce by a later well-meaning sweep, so test 1 below asserts **no exception
escapes the middleware** on an aborted request — that assertion exists specifically to fail if a
token is ever added here.

### Two things weighed and declined

- **Checking `RequestAborted` *before* `await next()`.** It would save the downstream work
  entirely, and is tempting. Declined: it is a larger behaviour change than the defect calls for
  (the inner pipeline stops running at all, so any side effect it has — caching, telemetry,
  `OnPrepareResponse` — silently disappears), it is not what was reported or reproduced, and the
  prerendering middleware is not the right component to decide whether the rest of the application
  should run. If someone wants it later it is a separate, arguable feature.
- **A partial copy under a full `ContentLength`.** Case (b) forwards fewer bytes than the
  `ContentLength` static files already set. Kestrel's mismatch check is `!_connectionAborted`-guarded
  (Spike 6), so on a genuine abort nothing throws. In the hypothetical where `RequestAborted` is
  cancelled while the connection is alive, a mismatch could surface — but a bare `return` has the
  *same* exposure (0 of N bytes), so this is not a reason to prefer one over the other. Noted, not
  mitigated.

### Behaviour nuance worth a release-note line

On case (a) nothing is written, so the response never starts, so `Response.OnStarting` never fires,
so `options.OnPrepareResponse` is **not** invoked on aborted requests. That is correct (there is no
response to prepare) but it is a visible difference from today, where the request proceeds to node.

---

## 2. G5 — the guard tests `IsNullOrWhiteSpace`, and nothing more

**Decision: the guard is exactly**

```csharp
// after canPrerender, on the decoded template, before customData is handed to anyone
if (string.IsNullOrWhiteSpace(originalHtml))
{
    // log (see §3), copy the buffer out, return
}
```

It runs **before** `OnSupplyData` and therefore before node, and it exits via the same
`copy → return` convention. It tests the *decoded string*, not `outputBuffer.Length`, so
whitespace-only is covered by the same expression — and it therefore consumes whatever the Defect-1
owner's decode produces (`GetString(buffer, 0, Length)` or `ToArray()`, BOM stripped or not). One
coupling to state plainly for M5: **the guard must be evaluated on the post-BOM-strip string**, or a
BOM-only file (3 bytes, no content) reads as non-empty and walks into node.

### What "unusable" means, and what it deliberately does not mean

An empty or whitespace-only template is *definitionally* unusable: there is no consumer, present or
future, who wanted `renderApplication` to be handed `""`. That is a fact about the value, not a
guess about content, which is why it is safe to act on silently-ish.

Everything beyond that is a guess, and the two candidate guesses were both rejected:

**`ContentLength` vs. actual bytes — rejected as control flow.** The signal is real (static files
sets `ContentLength` from the file length before sending, so a short buffer *is* detectable) and it
is the only mechanical way to spot case (b). It is still the wrong thing to branch on:

- **It has legitimate false positives inside `next()`.** Collateral finding #5 is the proof:
  `UseWebMarkupMin` sits inside `next()` and turns a 547-byte file into a 456-byte template. Any
  response-transforming middleware — minification, rewriting, a custom filter — legitimately makes
  buffer length ≠ the `ContentLength` some inner component set. A false positive here means
  **prerendering silently stops working** for that application, which is a far worse failure than
  the one being fixed, and much harder to diagnose.
- **It is absent exactly where it would be needed.** Chunked responses carry no `ContentLength` at
  all, which is the dev-proxy shape (Spike 4) — the second route to a truncated template. So the
  heuristic is unavailable in one of the two cases it was proposed for.
- **The case it *would* catch is already covered causally.** Truncation comes from an abort, and the
  abort check catches it at the source, before the template is even decoded. Branching on a
  downstream symptom when the cause is directly observable is the definition of too clever.

`ContentLength` does earn its keep — as **data in a log message**, not as a branch. See §3.

> **Reversed by the issue #80 investigation.** See
> [`SOLUTION-range-template-gate.md`](./SOLUTION-range-template-gate.md) §3.1.1, which reinstates
> this comparison as a rejection. The first argument above — the load-bearing one — does not hold:
> a transforming middleware that left a stale `ContentLength` behind would fail Kestrel's own
> Content-Length verification on **every** response, on every route this middleware never touches.
> `UseWebMarkupMin` updates the length as it minifies, which is why the demo works at all. So a
> mismatch means truncation or an unflushed writer, not transformation.
>
> The reinstated form is narrower than what was rejected here, and answers the other two objections
> rather than overriding them: it fires **only** when a length is declared (so the chunked
> dev-proxy shape is untouched, and the abort check remains its only cover), **only**
> one-directionally (captured < declared, since a longer capture cannot be a truncation), and it
> **logs at Warning** — the objection above was really about a *silent* false positive.
> `Prerenders_a_response_a_transforming_middleware_shrank` is the regression guard: if that test
> ever fails, this rejection was right and the reversal was wrong.

**A minimal structural check (`contains "<html"`, `</body>`, …) — rejected.** It buys almost
nothing: a template truncated mid-`<body>` still contains `<html`, so the check passes on the very
case it was invented for, while a legitimate consumer supplying a fragment rather than a full
document would be rejected. It encodes a policy about template shape that this middleware has never
had and has no business inventing, and its failure mode (skip prerendering) is silent degradation.

### What should be left to fail loudly

A **corrupt-but-non-empty** template that was not produced by an abort. There is no reliable test
for it, and the honest outcome is the one that already happens: node throws NG05104, the exception
surfaces as `NodeInvocationException` at `SpaPrerenderingExtensions.cs:168`, and the operator gets a
stack trace pointing at prerendering. Adding a guess-guard in front of that would replace a loud,
diagnosable failure with a quiet blank page. Fail loudly is the right answer here; that is the
whole reason the guard stops at `IsNullOrWhiteSpace`.

---

## 3. Logging — yes, and `LoggerFinder` is usable

### Current state

The middleware has **no logging whatsoever**. The only diagnostic output in the whole delegate is
two `Console.WriteLine` calls (lines 90, 92) around the BootModule build. Every guard exits
silently. That is exactly how Defect 2 stayed invisible for years, so the PRD's position stands and
silence is not being justified.

### `LoggerFinder` is reachable — confirmed by reading it

`Prerendering/Internals/LoggerFinder.cs` takes an `IApplicationBuilder` and falls back to
`NullLogger.Instance` when no `ILoggerFactory` is registered. `UseSpaPrerendering` already has
`applicationBuilder` in scope at line 53, so the logger is captured **once at setup**, outside the
`Use` delegate, alongise the other captured context (`nodeServices`, `applicationStoppingToken`, …)
— precisely the pattern `AngularPrerendererBuilder.cs:52` already uses:

```csharp
var logger = Internals.LoggerFinder.GetOrCreateLogger(applicationBuilder, nameof(UseSpaPrerendering));
```

The `NullLogger` fallback matters for the test harness, which builds a `ServiceCollection` with no
logging registered — no test setup change is needed, and no null checks at the callsites.

Category name: `nameof(UseSpaPrerendering)` matches this repo's existing convention
(`AngularPrerendererBuilder` uses `nameof(AngularPrerendererBuilder)`; `AngularCliMiddleware` uses a
`LogCategoryName` const). Either is fine; be consistent with one and do not invent a third shape.

### The two messages and their levels

| Event | Level | Rationale |
|---|---|---|
| Prerendering skipped because the request was aborted | **Debug** | Client-driven and routine. Spike 5 measured 175 real aborts in a 200-request burst; a `Warning` per navigate-away is log spam that trains operators to filter the category, which costs more visibility than it buys. `Debug` is not silence — it is there the moment anyone looks, which is all this needed. |
| Prerendering skipped because the captured template was empty or whitespace | **Warning** | There is **no known benign cause** once the abort path is handled. If this fires, something the project does not understand is happening, and the operator should see it without opting in. It is also self-limiting: it fires only on a path that previously ended in an NG05104 500. |

Explicitly **not** logged: the existing `!canPrerender` exit (line 138). It is the normal
dev-server path for every static asset — the code comment already says so — and logging it would
emit a line per request in development.

### This is where `ContentLength` belongs

The abort-path `Debug` message should carry the diagnostic data the rejected heuristic would have
computed, so case (b) is *diagnosable* without being *actionable by the middleware*:

> `Skipping prerendering for {Path}: the request was aborted. Captured {CapturedBytes} of {ContentLength} declared bytes.`

That distinguishes case (a) (`0 of 20000`) from (b) (`8192 of 20000`) from (c) (`20000 of 20000`) in
one line, at zero risk, and is the reason `ContentLength` needed a decision rather than a dismissal.
Use structured placeholders; no `LoggerMessage` source generation — nothing else in this repo uses
it, and two call sites do not justify introducing the pattern.

---

## 4. Line-161 bypass (collateral finding #1) — **IN SCOPE**, with the minimal-risk fix

### Why in scope

It is a defect in the same guard, five lines from the abort check, on the same file, reachable from
this repo's own `MintPlayer.AspNetCore.SpaServices.Routing` package, and it makes `GET /` on the
demo return **301 with a 24,530-byte prerendered body** — a wasted node render per redirect, on
every request that redirects this way. The one-PR policy forbids parking it, and "leave the guard
weaker than it looks" is not a decision anyone would ratify explicitly.

### The mechanism, and why the obvious fix is the wrong one

`SpaRouteService.Redirect` (`Routing/Services/SpaRouteService.cs:113` and `:126`) defers its
`Response.Redirect` into `Response.OnStarting`. Reading the middleware explains **why**:
`ServePrerenderResult` opens with `context.Response.Clear()` (line 243), which resets the status
code and drops headers. Any status set synchronously inside `OnSupplyData` would be wiped. So the
deferral is not sloppiness — it is a workaround for the middleware not honouring a status set at the
seam, and the two components are working against each other.

The obvious fix — make `Redirect` set status + `Location` **eagerly** so line 161 sees the 301 — was
seriously considered and **declined**:

- It is a behavioural change to a shipped public API (`ISpaRouteService.Redirect`) for every
  consumer, including ones not using prerendering at all.
- It churns `Tests/Routing/RedirectTests.cs`, which asserts the deferred shape today, and that
  suite's whole reason for existing is a real bug (302 clobbering a permanent redirect) that the
  deferral currently protects.
- **It is not testable at this seam.** Spike 1's side-finding is decisive: `Response.OnStarting` is
  a no-op on `DefaultHttpContext`, so a status-based fix cannot be observed by the harness at all.
  A fix that the committed tests cannot see is not a fix.

### Recommended fix: an explicit, synchronously observable opt-out

Add a small public opt-out to the **Prerendering** package (the dependency direction allows it —
`Routing` references `Prerendering`, not the reverse):

- `HttpContext.SkipPrerendering()` (extension method) sets a sentinel in `HttpContext.Items` under a
  private key; an internal `IsPrerenderingSkipped(context)` reads it.
- The middleware checks it **alongside** the line-161 status re-check, i.e. the guard becomes
  "status is no longer 2xx **or** the consumer asked us to skip" → `copy → return`.
- `SpaRouteService.Redirect` (both overloads) calls it **in addition to** the existing `OnStarting`
  deferral. Nothing about the deferral changes, so no existing consumer or test moves.

Why this shape:

- **Additive and synchronous.** Zero behaviour change for anyone who does not call it, and it is
  visible to the middleware at line 161 without the response having started — which is the exact
  property the status code lacks.
- **It fixes the general problem, not just ours.** Any consumer that mutates the response from
  `OnStarting`, or that simply knows this request should not be prerendered, gets a supported way to
  say so. Today they have none. This is the guard the middleware should have had.
- **It is testable.** `HttpContext.Items` works on `DefaultHttpContext`, so test 9 below asserts it
  directly.

Cost: one new public extension method on a shipped package (purely additive), plus two call sites in
`Routing`.

### The honest limitation

This does not make line 161 detect a *third-party* `OnStarting`-deferred status change — that is
undetectable from middleware by construction, since the whole point of `OnStarting` is that it has
not run yet. The fix converts an invisible trap into a documented opt-out, and the XML doc on the
new method should say so in one sentence. Anyone deferring a status change without calling it still
gets a prerendered body under their redirect, and that is now their decision rather than a silent
surprise.

---

## 5. Tests

All of these belong in `MintPlayer.AspNetCore.SpaServices.Tests/Prerendering/`, built on
`SpikeHarnessTests.cs`'s `PrerenderingHarness`. They must be committed **before** the fix so the
diff shows red → green.

### One harness change is required

`PrerenderingHarness.Run` grows an optional `Action<DefaultHttpContext>? configureContext`, applied
after `PrerenderingTestContext.Create(...)` and before `await pipeline(context)`. That is the whole
mechanism: `DefaultHttpContext.RequestAborted` is settable (Spike 1 side-finding — the setter goes
through `IHttpRequestLifetimeFeature`, which `DefaultHttpContext` materialises on demand), so
**no socket, no `TestServer`, no real abort is needed.**

An inner pipeline that models the static-files abort contract exactly:

```csharp
// 200 → text/html → ContentLength set → N body bytes written (N may be 0) → returns normally
public static RequestDelegate StaticFileAbortContract(int declaredLength, int bytesWritten)
```

`ExplodingNodeServices` (already in the harness) is what asserts "the prerenderer is NOT invoked" —
it throws if node is reached, so every test below gets that assertion for free by not throwing.
`RecordingPrerenderingService.WasCalled` is what asserts "`OnSupplyData` is NOT called".

### The committed tests

| # | Test | Setup | Asserts |
|---|---|---|---|
| 1 | `Aborted_static_file_request_does_not_prerender` — **the G2 test** | `StaticFileAbortContract(declaredLength: 20000, bytesWritten: 0)`, `RequestAborted` = an already-cancelled token | **No exception escapes the middleware** (this is the assertion that locks in "no token in `CopyToAsync`" — with a token, `MemoryStream.CopyToAsync` front-checks and throws even at 0 bytes); `Service.WasCalled == false`; `ClientBody.Length == 0`; status still 200; `ContentType` still `text/html`. Fails on `master` (today `WasCalled == true` with `originalHtml == ""`). |
| 2 | `Aborted_request_with_a_complete_body_still_passes_the_body_through` | full template written, `RequestAborted` cancelled (case (c)) | `ClientBody` equals the body **byte for byte**; `WasCalled == false`. This is the test that justifies copy-then-return over the reporter's bare `return;` — it fails against a bare `return`. |
| 3 | `Truncated_template_from_a_mid_copy_abort_does_not_prerender` | `StaticFileAbortContract(20000, 8192)`, `RequestAborted` cancelled (case (b)) | `WasCalled == false`; `ClientBody` equals the 8192 written bytes; no exception. Locks in that **the abort check, not the empty check**, is what covers partials. |
| 4 | `Empty_template_does_not_reach_the_prerenderer` — **the G5 test** | `StaticFileAbortContract(20000, 0)`, `RequestAborted` **not** cancelled | `WasCalled == false`; no exception; `ClientBody.Length == 0`; status 200. This is the guard proper, on the hypothetical third path. |
| 5 | `Whitespace_only_template_does_not_reach_the_prerenderer` | body `"\r\n   \r\n"`, not aborted | `WasCalled == false`; `ClientBody` equals those bytes. Pins the `IsNullOrWhiteSpace` half of the decision. |
| 6 | `Proxy_style_response_without_a_content_type_does_not_prerender` — **the negative proxy assertion** | 200, `ContentType == null`, 362 bytes written, `RequestAborted` cancelled | `WasCalled == false`; body passed through unchanged. Locks in the `canPrerender` behaviour that makes development safe (Spike 4: 0 of 400 real dev aborts reproduced) — so a future change to `canPrerender` cannot quietly make dev unsafe. |
| 7 | `Truncated_template_is_not_rejected_when_the_request_was_not_aborted` | `StaticFileAbortContract(20000, 8192)`, **not** aborted | `WasCalled == true` and `originalHtml` is the 8192-byte string. This pins the *deliberate non-decision* in §2: we do **not** branch on `ContentLength` vs. actual bytes. Without it, the rejected heuristic reads as an oversight and gets "fixed" later. |
| 8 | `A_normal_html_response_still_reaches_the_prerenderer` | existing happy path, not aborted, non-empty body | `WasCalled == true`. Guards against the new guards over-firing. Partly covered by the existing `Captures_the_original_html_without_launching_node`; keep both. |
| 9 | `A_consumer_that_opts_out_does_not_reach_the_prerenderer` (only if §4 is accepted) | a service whose `OnSupplyData` defers a 301 via `Response.OnStarting` **and** calls `SkipPrerendering()` | node not reached (`ExplodingNodeServices` does not throw) and the body is passed through, not prerendered. Note in a comment that the `OnStarting` half is a no-op under `DefaultHttpContext` — which is exactly why the fix could not be status-based. |

### Coupling to flag for M5

`SpikeHarnessTests.Shows_the_GetBuffer_padding_defect_verbatim` asserts the padding **is present**
(`Assert.Equal(new string('\0', 180), …)`). It is an observation test written during M2 and it
**must be inverted or deleted** by whoever implements Defect 1 — it will fail once the decode is
fixed. Not this workstream's file to change, but it will break the M5 sweep if nobody owns it.

Also note per the plan's M3 trap: none of the tests above depend on template size for their own
correctness (they are abort/emptiness tests, not padding tests), so the >16 KB requirement applies
to the Defect-1 tests only.

---

## Summary of decisions

| # | Decision |
|---|---|
| 1 | **Confirm Spike 6.** Own early return after the `finally`, before `canPrerender`, `await outputBuffer.CopyToAsync(context.Response.Body)` then `return`. No token in that copy — test 1 enforces it. Declined: a pre-`next()` abort check. |
| 2 | **G5 guard = `string.IsNullOrWhiteSpace` on the decoded template, and nothing more.** Rejected `ContentLength`-vs-actual as control flow (legitimate false positives from response-transforming middleware; absent on chunked/dev; the case it catches is already covered causally) and rejected structural checks (detects the wrong things, invents policy). Corrupt-but-non-empty templates are left to fail loudly as NG05104. |
| 3 | **Log both skips.** `LoggerFinder` **is** usable — capture once at setup from `applicationBuilder`, `NullLogger` fallback keeps the harness working. Abort skip → `Debug` (routine, high volume) carrying `captured N of M declared bytes`, which is where `ContentLength` earns its keep. Empty template → `Warning` (no known benign cause). The `!canPrerender` exit stays unlogged. The middleware has **zero** logging today. |
| 4 | **Line-161 bypass is in scope.** Fix with an additive, synchronously observable `HttpContext.SkipPrerendering()` opt-out in the Prerendering package, checked next to the line-161 status re-check and called by `SpaRouteService.Redirect`'s two overloads alongside their existing `OnStarting` deferral. Declined making `Redirect` eager (public behaviour change, test churn, and unobservable under `DefaultHttpContext`). Third-party deferred status changes remain undetectable by construction — documented, not fixed. |
| 5 | **Nine tests**, one harness addition (`configureContext`), no socket work. Includes the negative proxy assertion and a test that deliberately pins the rejected `ContentLength` heuristic as rejected. |
