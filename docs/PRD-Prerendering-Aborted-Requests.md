# PRD: Corrupt / empty `originalHtml` in `UseSpaPrerendering`

Upstream report: [PR #78](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/78)
by @Reonekot (branch `bugfix/corrupt-originalhtml`).

## Overview

`UseSpaPrerendering` captures the downstream response into a `MemoryStream` and hands it to the
node prerenderer as the `originalHtml` template. A production user reports two independent defects
in that capture, both surfacing as the same symptom: Angular's SSR bootstrap throws **NG05104**
because the template it was given is empty or corrupt.

The reporter has no reproduction — they believe it is a race condition and could not emulate
browser aborts from a console app. This document covers reproducing **both** defects
deterministically, then fixing them, in a single PR.

## Problem Statement

All the affected code is
[`SpaPrerenderingExtensions.UseSpaPrerendering`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs),
lines 108-180.

### Defect 1 — `GetBuffer()` is read without a length

```csharp
{ "originalHtml", Encoding.UTF8.GetString(outputBuffer.GetBuffer()) }   // line 149
```

`MemoryStream.GetBuffer()` returns the stream's **internal array**, whose length is `Capacity`, not
`Length`. A growable `MemoryStream` doubles its capacity (256, 512, 1024, …), so for any response
whose byte count is not exactly a capacity boundary the decoded string carries trailing padding
bytes that were never part of the response.

Two things about this are worth stating up front, because they shape the whole investigation
(both now settled by **Spike 2**):

1. **It is not intermittent — but it does not fire on every request either.** It is a deterministic
   function of body length *and write chunking*, with nothing to do with load or timing. The real
   static-file path writes in 16 KB chunks (`SendFileFallback`, buffer `1024*16`), and
   `MemoryStream.EnsureCapacity` uses `max(requested, 256)` before doubling — so a single write of
   256 B…16 KB lands on `Capacity == Length` exactly and pads **nothing**. Measured: **~0% of
   responses between 256 B and 16 KB are corrupted, and ~100% of responses over 16 KB are** (only
   32768/65536/131072 escape). Under 256 B also pads.
2. **It cannot truncate. Proven, and the claim is retired.** `GetBuffer().Length >= Length` is a
   structural invariant of a growable `MemoryStream`; a 200,000-stream × 6-random-op fuzz found zero
   violations. If the reporter genuinely saw truncation, it came from Defect 2 (empty body), not
   from this line.

The padding decodes to **genuine `\0`, always, in this code path** — `new MemoryStream()` allocates
a fresh CLR-zeroed array on each grow and copies only `_length` bytes forward. Stale non-zero bytes
*are* reachable, but only by shrinking and re-writing the same stream instance
(`SetLength(0)` + rewrite leaves the old tail readable); this middleware creates a fresh stream per
request and never shrinks it, so "stale/garbage bytes" is **not** reachable here. The only
hypothetical is a downstream component that resets and re-writes `Response.Body` — noted, not
reproducible through the real pipeline.

That supports the severity read: NUL padding after `</html>` is reparented into `<body>` by normal
HTML parsing and survives to the browser, which is why this went unnoticed for years. It is **not**
a credible cause of NG05104 (that needs an empty template — Defect 2). What it does cause is a
~11 KB run of NULs shipped to the client on a 120 KB template, and breakage of anything doing
exact-match on the HTML: response-hash caching, ETag/diff comparison, validators, snapshot tests.

### ⚠ Test-design trap (from Spike 2 — read before writing the G1 test)

The demo's own `ClientApp/dist/browser/index.html` is **547 bytes**, written in one 547-byte write
→ capacity 547 → **zero padding**. A regression test that asserts "`originalHtml` equals the body
exactly" against a realistic small template **passes on `master` with the bug fully present**. The
G1 test therefore MUST use a template that is >16 KB, or <256 bytes, or a fake inner pipeline that
writes in ≥2 chunks. (A realistic `ng build` `index.html` with inlined critical CSS, 5-60 KB, is
squarely in the padded zone — so production users are hit and the demo app is not.)

### Defect 1b — UTF-8 BOM is not stripped (separate small fix)

Independent of the padding and **not fixed** by adding a length to `GetString`: `Encoding.UTF8.GetString`
does not strip a byte-order mark. If `index.html` is saved as UTF-8-with-BOM (Visual Studio's
default for new HTML files on Windows), `originalHtml` begins with U+FEFF, which is handed to
`renderApplication({ document })` as a stray character before `<!doctype>`. The demo's two
`index.html` files have no BOM (verified: first bytes `3c 21 64`), so this is latent here, but it is
a real user-facing case and earns its own fix rather than a footnote.

### Defect 2 — prerendering proceeds after the client has aborted

When the client disconnects mid-response, the reporter observes: `Response.ContentType` and
`Response.ContentLength` are set, the body is **never written**, no exception escapes, and the
middleware walks straight into `IsSuccessStatusCode && IsHtmlContentType` → both true → node is
invoked with `originalHtml == ""` → NG05104.

**Spike 3 confirmed the mechanism against `dotnet/aspnetcore` `release/10.0`**, with one correction:
`StaticFileContext.SendAsync` swallows and *logs* the `OperationCanceledException` — it does **not**
call `context.Abort()`; there is no `Abort()` call anywhere in the static-files middleware. The
swallow is unconditional:

```csharp
await ApplyResponseHeadersAsync(StatusCodes.Status200OK);   // ContentType + ContentLength set HERE
try { await _context.Response.SendFileAsync(_fileInfo, 0, _length, _context.RequestAborted); }
catch (OperationCanceledException ex) { _logger.WriteCancelled(ex); }   // swallowed, never rethrown
```

### The contract the reproduction test must model

After `await next()` on an aborted request (Kestrel, static files as inner terminal):

| Observable | Value |
|---|---|
| Exception out of `await next()` | **none** |
| `StatusCode` | **200** |
| `ContentType` | **set** (`text/html`) |
| `ContentLength` | **set** to the file's length |
| Bytes in the `MemoryStream` | **0**, **partial**, or **all** — see below |
| `RequestAborted.IsCancellationRequested` | **true** |
| → `canPrerender` | **true**, so node is invoked |

### Three reachable outcomes, and why this matters more than expected

`StreamCopyOperationInternal`'s loop returns when `bytesRemaining` hits 0 *before* its next
cancellation check, so **when** the abort lands decides what is in the buffer:

- **(a) abort already signalled on entry** → **0 bytes**. `SendFileFallback`'s
  `ThrowIfCancellationRequested` (or `MemoryStream.FlushAsync`) throws immediately. **This is the
  reported defect, and the case the test should model.**
- **(b) abort mid-copy** → **partial bytes** for a file larger than the 16 KiB copy buffer.
- **(c) abort after the last chunk** → **full buffer, no OCE at all**. An `index.html` under 16 KiB
  is one read + one write, so this window is not narrow — meaning **not every aborted request
  produces a broken template**, which fits the "intermittent" story better than a uniform failure
  would.

**Case (b) reconciles the reporter's "truncated content" claim.** Spike 2 proved truncation is
unreachable *through `GetBuffer()`* — and that stands. But it is reachable *here*, through a partial
copy. So the reporter saw a real thing; they attributed it to the wrong line.

**This has a direct design consequence for G5:** an empty-template guard would **not** catch case
(b). A half-written `index.html` is non-empty, passes any `IsNullOrWhiteSpace` check, and still
breaks SSR. So the abort check is *not* redundant with the empty check — they cover different
cases, and the `RequestAborted` check is the only one that covers (b). This retires the plan's open
question of whether the two guards overlap: they do not.

Spike 4 still owns the equivalent question for this repo's dev-server proxy path — in development
the template comes from the proxy, not from `StaticFileMiddleware`.

### Why this is worth fixing beyond the reporter's patch

Independent of either root cause, the middleware has no defence against a template it cannot use.
An empty or whitespace-only `originalHtml` is never something the prerenderer can do anything with,
yet it is passed through to `OnSupplyData` and to node regardless. A guard there is cheap and
covers whatever *third* path produces an empty buffer that nobody has found yet.

## Workstream 3 — cancellation-token threading (independent)

**This is not a fix for either reported defect and must not be justified by them.** It is a separate
piece of work that happens to live in the same files, requested on its own merits: the request's
cancellation token should reach everywhere it is needed. It rides along in the same PR (one-PR
policy), but its rationale, its tests and its risk are its own.

### What was found by inspection (pre-spike, already verified by reading the code)

1. **The node RPC gets no token at all.**
   [`Prerenderer.cs:76`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/Prerenderer.cs)
   calls `nodeServices.InvokeExportAsync<RenderToStringResult>(scriptFile, "renderToString", …)`.
   `INodeServices` exposes overload *pairs* — one with a leading `CancellationToken` and one
   without — and this callsite binds to the **token-less** overload, which hard-codes
   `CancellationToken.None`
   ([`NodeServicesImpl.cs:41`](../MintPlayer.AspNetCore.NodeServices/NodeServicesImpl.cs)). So not
   even `applicationStoppingToken` reaches the RPC; that token is used *only* to schedule temp-file
   cleanup in `GetNodeScriptFilename`. A prerender in flight cannot be cancelled by anything.

2. **The plumbing below the callsite is already correct.**
   `OutOfProcessNodeInstance.InvokeExportAsync` links the incoming token with its own timeout
   source (`OutOfProcessNodeInstance.cs:110-120`), and `NodeServicesImpl` forwards the token through
   its retry path. The gap is purely at the top: nobody supplies a token.

3. **`SpaProxy` is the model to copy, not to fix.**
   `SpaProxy.PerformProxyRequest` (`SpaProxy.cs:58-61`) already does the right thing —
   `CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStoppingToken)`
   — and threads that linked token through `SendAsync`, `CopyProxyHttpResponse` and the WebSocket
   pump. The prerendering middleware should look like this and does not.

4. **Token-less async calls in the prerendering middleware.**
   `outputBuffer.CopyToAsync(context.Response.Body)` (`SpaPrerenderingExtensions.cs:140` and `:163`)
   and `context.Response.WriteAsync(renderResult.Html)` (`:283`) all take no token.

5. **The overload pairs are a latent footgun.** Because both overloads end in
   `params object[] args`, omitting the token is silent — there is no diagnostic, and item 1 is
   almost certainly an accident rather than a decision. Worth deciding whether the token-less
   overloads should be `[Obsolete]`, or whether the fix is confined to callsites.

### Spike 8 result — **G7 is safe to do** (it was gated on this)

- **A cancelled RPC does not trigger a retry.** `NodeServicesImpl`'s retry `catch` filters on
  `NodeInvocationException`; a cancellation is a `TaskCanceledException` and is not caught there at
  all. Even in the timeout/cancel race where a cancellation *is* converted, both conversion sites
  use the two-argument `NodeInvocationException` constructor, so `NodeInstanceUnavailable` defaults
  to `false`. The only `nodeInstanceUnavailable: true` in the repo is guarded by
  `if (_nodeProcess.HasExited || _nodeProcessNeedsRestart)` — a process-state pre-flight,
  independent of any token. **No teardown, no 15s draining, no respawn on navigate-away.**
- **Cancellation vs timeout are cleanly distinguished:** cancellation → `OperationCanceledException`,
  timeout → `NodeInvocationException`. One cosmetic race remains: if the abort lands in the same
  window as the 60s invocation timeout, the user gets the misleading "Node invocation timed out …
  ensure your Node.js function always invokes the supplied callback" message for what was a client
  disconnect. Harmless, but it will start appearing in production logs once cancellations become
  reachable.
- **But cancelling is pure abandonment.** The RPC protocol has no abort channel:
  `HttpNodeInstanceEntryPoint.ts` registers no `aborted`/`close` handler, `renderToStringImpl` gets
  no signal, and Angular's `renderApplication` is never handed an `AbortSignal`. Node finishes the
  entire render and writes to a dead socket. **What G7 buys is the .NET request thread and the
  middleware continuation back — not reclaimed node CPU.** Worth stating plainly so the value isn't
  oversold.

### ⚠ Spike 7 trap — do NOT replace `applicationStoppingToken` with a linked token

The obvious-looking refactor of `Prerenderer.RenderToString` — swap its `applicationStoppingToken`
parameter for a pre-linked one — **introduces a new bug**. That parameter has a second consumer:
it is forwarded to `GetNodeScriptFilename` → `new StringAsTempFile(script, applicationStoppingToken)`,
which registers `EnsureTempFileDeleted` on it. `NodeScript` is a **`static`** field built once under
a lock and reused for the whole process. Link `RequestAborted` in and the first request to finish or
abort **deletes the shared `prerenderer.js` temp file**; every later render then fails on a missing
script, and because `NodeScript != null` it is never recreated.

The two consumers need different lifetimes. **Add** a distinct request-scoped parameter forwarded
only to the token-taking `InvokeExportAsync`, and leave `applicationStoppingToken` alone.
`Prerenderer` is an `internal static class`, so neither shape is a breaking change for consumers
(only `SpaPrerenderingExtensions` and the test assembly call it).

### Spike 7 audit — one genuinely wrong callsite, not a sweep

Everything else that greps as "missing a token" is deliberate: `EventedStreamReader`'s pumps must
outlive requests and drain to EOF, `StringAsTempFile` is process-lifetime cleanup,
`NodeServicesImpl`'s `Task.Delay` *is* the connection-draining window, `BootModuleBuilder.Build` is a
one-shot startup shared by all requests (handing it the first request's token would abort the build
for everyone), and `next()`/`OnSupplyData`/`OnPrepareResponse` carry the token via `HttpContext`.

| Callsite | Verdict |
|---|---|
| `Prerenderer.cs:76` node RPC | **Wrong — fix.** Linked `RequestAborted` + `ApplicationStopping`. |
| `SpaPrerenderingExtensions.cs:140`, `:163` `CopyToAsync` | Cosmetic. Note Spike 6: on the *abort* path a token would convert a silent no-op into a thrown OCE. |
| `SpaPrerenderingExtensions.cs:283` `WriteAsync` | Cosmetic; token overload exists. |

### G8 recommendation — keep the token-less overloads, document them

`[Obsolete]` would emit `CS0618` in every consumer that calls a *correct* API — plenty of node
invocations have no request to scope to — exporting our one-off internal mistake to every user;
`[Obsolete(error: true)]` or removal would be source-breaking on a shipped public interface. The
overloads' XML docs currently don't mention cancellation at all, which is the actual gap: state that
they invoke with `CancellationToken.None` and point at the token-taking overload for request-scoped
work.

### Workstream 4 — the unwired SSR build timeout (independent, **in scope** by user decision)

The `catch (OperationCanceledException)` below is **confirmed dead code**: the `try` awaits only
`EventedStreamReader.WaitForMatch`, whose TCS resolves solely via `SetResult(match)` or
`SetException(new EndOfStreamException())`, and there is no `SetCanceled`/`TrySetCanceled` anywhere
in the repo — so the awaited task can never be `Canceled`. The user has authorised including the
refactor on that basis.

⚠ **The trap:** naively wiring `WithTimeout` would make the *other* catch dead too.
`TaskTimeoutExtensions.WithTimeout` propagates faults via `task.Wait()`, which wraps them in
`AggregateException` (already pinned by `PrerenderingInternalsTests.cs:238` and
`TaskTimeoutExtensionsTests.cs:50,61`), so `EndOfStreamException` would no longer match line 74 and
the "script exited without indicating success" diagnostic — which reports the npm stdout/stderr —
would be silently lost. `WithTimeout` also throws `TimeoutException`, not
`OperationCanceledException`, so line 81 would stay dead anyway. Any fix must keep **both**
diagnostics reachable and say how each is reached.

### Adjacent finding (context for Workstream 4)

`AngularPrerendererBuilder.cs:71` `await scriptRunner.StdOut.WaitForMatch(finishedRegex)` is the
package's only **unbounded** await — it resolves on a match or `EndOfStreamException`, nothing else.
Meanwhile `SpaPrerenderingExtensions.cs:62` computes `var buildTimeout = spaBuilder.Options.StartupTimeout;`
and **never uses it**, this package's own `Extensions/TaskTimeoutExtensions.cs` is entirely
unreferenced, and the `catch (OperationCanceledException)` at `AngularPrerendererBuilder.cs:81` is
consequently dead code. A hung `ng build --watch` therefore hangs the first request forever. Not
request-scoped work, so not part of G7 — raising it for a decision rather than folding it in.

## Goals

| # | Goal |
|---|---|
| G1 | Reproduce Defect 1 deterministically in an automated test that fails on `master`. |
| G2 | Reproduce Defect 2 deterministically in an automated test that fails on `master`. |
| G3 | Reproduce Defect 2 (or prove it unreachable) in the real `Demo.Web` app under `dotnet run`. |
| G4 | Fix both, with the fixes covered by the tests from G1/G2. |
| G5 | Decide on and implement the empty-template guard described above. |
| G6 | Land it all in one PR, and close/absorb PR #78 with credit to the reporter. |
| G7 | Thread a request-scoped cancellation token (linked: `RequestAborted` + `ApplicationStopping`) through the prerendering path to every call that accepts one — the node RPC above all. Independent of G1-G5. |
| G8 | Decide the public-API question G7 raises: do the token-less `INodeServices` overloads stay, get obsoleted, or get documented as deliberate? This is a shipped public interface, so the answer is a compatibility decision, not a style one. |

## Non-Goals

- Rewriting the response-capture strategy (e.g. moving to `IHttpResponseBodyFeature` /
  `PipeWriter`). Called out as a design option in the solution phase, but a rewrite is not the ask.
- Any change to the node/`Prerenderer` transport.
- Reproducing the failure against IIS. The reporter notes IIS and Kestrel may abort differently;
  we target Kestrel and note IIS as unverified.

## Reproduction strategy

The reporter's difficulty was trying to reproduce this from the *outside* — racing a real browser
abort against a real static-file send. The strategy here is to reproduce it from the *inside*, at
the seam the middleware actually depends on, plus one real-app confirmation.

### The `ISpaPrerenderingService` seam

The middleware's own control flow gives us a node-free observation point. `OnSupplyData` is called
with the `customData` dictionary **before** node is invoked (line 157), and immediately afterwards
the middleware re-checks the status code and bails out if it is not 2xx (line 161):

```
capture body → canPrerender? → build customData{originalHtml} → OnSupplyData → status still 2xx? → node
                                                                     ▲                 │
                                                    capture originalHtml here    set 302 here to bail
```

So a test `ISpaPrerenderingService` can record `customData["originalHtml"]` verbatim **and** set
`context.Response.StatusCode = 302` to make the middleware return before it ever reaches
`Prerenderer.RenderToString`. That gives us assertions on the exact string the prerenderer would
have received, with no node process and no `nodeServices` round trip. Spike 1 owns proving this
works.

### Existing test suite

`MintPlayer.AspNetCore.SpaServices.Tests` (xunit, `net10.0`, `FrameworkReference
Microsoft.AspNetCore.App`) already tests this file, but only its private static helpers via
reflection (`SpaPrerenderingReflection`, `PrerenderingTestContext`). Nothing exercises the
middleware delegate itself. Both new tests need that delegate, which is the one real obstacle —
`UseSpaPrerendering` resolves `INodeServices`, `IHostApplicationLifetime` and `IWebHostEnvironment`
**eagerly at build time** (lines 55-57).

## Spikes

Each spike is a question with a falsifiable answer. Results get written back into this table.

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Can the `UseSpaPrerendering` delegate be built and invoked in a unit test without launching node? | **✅ Yes.** `CreateNodeServices` is lazy — `Process.Start` lives inside the node-instance factory lambda (`OutOfProcessNodeInstance.cs:72`), invoked only from `InvokeExportWithPossibleRetryAsync`. Verified empirically too: the pipeline runs with **no** `INodeServices` registered and the `node` process count is unchanged. Harness in `Tests/Prerendering/SpikeHarnessTests.cs`, 4/4 green. The trick: register the fake `next` via `applicationBuilder.Run(...)` **after** `UseSpaPrerendering`, else `next()` hits `ApplicationBuilder`'s 404 terminal. Side findings: `HttpContext.RequestAborted` is settable on `DefaultHttpContext` (so Defect 2 needs no socket work), and `Response.OnStarting` is a no-op there, making `options.OnPrepareResponse` unreachable without `TestServer`. |
| 2 | What does the `GetBuffer()` padding decode to? Is truncation reachable? BOM? Real padding length? | **✅ Answered — see Defect 1 above.** Padding is always `\0` here; stale bytes need a shrink this middleware never does. Truncation **unreachable** (fuzz-proven) — claim retired. Padding fires only >16 KB or <256 B; the demo's 547-byte template pads **zero**, which is the test-design trap called out above. BOM is not stripped → Defect 1b. |
| 3 | Confirm the abort contract against the real ASP.NET Core source. | **✅ Confirmed** (pinned to `release/10.0` @ `668a5ab`) — see the contract table above. Corrections: no `context.Abort()` (retired); the token passed to `SendFileAsync` **is** `context.RequestAborted` explicitly, which is load-bearing because `SendFileResponseExtensions`' own `catch … when (useRequestAborted)` filter then does *not* match and the OCE propagates to `StaticFileContext`'s catch instead; and the substituted `Response.Body` is **not** a special path — Kestrel itself routes `SendFileAsync` through `SendFileFallback`, so the only difference is the destination `Stream` plus one extra `StartAsync`/`FlushAsync`. All three token checkpoints throw rather than write silently. |
| 4 | Dev proxy vs. static files: does the proxy swallow cancellation the same way? Is Defect 2 dev-only, prod-only, or both? | **✅ Prod-reachable in practice, dev-reachable only in theory.** The proxy *does* swallow identically (`SpaProxy.cs:101-105` `catch (OperationCanceledException) { return true; }` → `ConditionalProxyMiddleware` returns without calling `next` and without throwing), but it sets **no headers first**: `CopyProxyHttpResponse` copies status + `Content-Type` and then immediately writes the body, so a pre-send cancellation leaves `ContentType == null` and the existing `canPrerender` check already rejects it. The dev window is the few microseconds between the header copy and the single 362-byte write — unhittable in 400 real aborts and in two forced-timing probes. Also: `SpaDefaultPageMiddleware` is **dead code in development** — `AngularCliMiddleware` registers a terminal `applicationBuilder.Run(...)` with `proxy404s: true` that never calls `next`. |
| 5 | Baseline SSR, then reproduce the abort as (a) mechanism and (b) real browser trigger. | **✅ BOTH REPRODUCED — NG05104 confirmed verbatim.** Baseline: Angular **21.1.0**, `GET /person` → 200, 25150 bytes of genuinely prerendered markup. **(a)** forced probe in Production: `strLen=0`, `status=200`, `ctype=text/html`, `aborted=True` → `OnSupplyData` with `originalHtml == ""` → `RuntimeError: NG05104 … at DefaultDomRenderer2.selectRootElement`, surfaced as `NodeInvocationException` at `SpaPrerenderingExtensions.cs:168` → HTTP 500. Deterministic. **(b)** real Chromium `fetch` + `AbortController` bursts, **no forced token**: **11 of 200 requests (5.5%; 6.3% of the 175 actual aborts)** produced an empty-template prerender → 11 × NG05104 → 11 × HTTP 500. In **Development: 0 of 400** real aborts reproduced (see Spike 4). |
| 6 | Is the reporter's `return;` on abort correct? Does `CopyToAsync` throw on an aborted request? Does `Response.Clear()` throw? | **✅ Answered. Nothing on the outbound path throws.** Kestrel *silently discards* writes after an abort (`HttpResponsePipeWriter.ValidateState`: "Aborted state only throws on write if cancellationToken requests it"; `Http1OutputProducer` early-returns once `_pipeWriterCompleted`), and the ContentLength-mismatch check is itself `!_connectionAborted`-guarded — so "headers but no body" is confirmed harmless, retiring that PRD risk. `CopyToAsync()` uses `CancellationToken.None` and additionally short-circuits at `if (n == 0)`, so on case (a) it touches `Response.Body` not at all. `Clear()` throws only on `HasStarted`, which is `false` here — `HasStarted` reads `IHttpResponseFeature`, and swapping `Body` replaces only `IHttpResponseBodyFeature`, so no downstream write can reach `ProduceStart`. **Recommendation: copy-then-return in its own early return, not a bare `return`** — see Proposed solution. |
| 7 | **(W3)** Audit every token-less call; which token should each get? | **✅ Done — exactly one is genuinely wrong** (`Prerenderer.cs:76`), plus three cosmetic response-write calls. All other hits are deliberate. Plus the `applicationStoppingToken` / `StringAsTempFile` trap and the G8 recommendation — see Workstream 3 above. |
| 8 | **(W3)** Does a cancelled RPC get retried / mistaken for a dead instance? Does it propagate or abandon? | **✅ G7 is NOT gated shut.** No retry is possible (the `catch` filters on `NodeInvocationException`; a cancellation is a `TaskCanceledException`). Cancellation and timeout are cleanly distinguished. But cancelling is pure abandonment — no abort channel exists in the RPC protocol, so node completes the render regardless; G7 recovers the .NET thread only. Details above. |

## Collateral findings (found while reproducing; each needs a decision)

None of these were in the original report. They are recorded here rather than silently fixed or
silently dropped.

1. **The line-161 status guard is bypassed by `OnStarting`-deferred status changes.** `GET /` on the
   demo returns **301 → /person with a 24530-byte prerendered body**. The demo's `OnSupplyData`
   calls `spaRouteService.Redirect`, which sets the 301 inside `Response.OnStarting` — so at line
   161 the status is still 200 and the `!IsSuccessStatusCode` bail-out never fires. The guard added
   at line 161 is therefore weaker than it looks against any consumer that redirects this way.
2. **Post-failure cascade.** After the NG05104 throw, `UseExceptionHandler("/Error")` re-enters the
   pipeline with the still-cancelled token and Kestrel then throws
   `InvalidOperationException: Response Content-Length mismatch: too few bytes written (0 of 547)`.
   So one aborted request produces two logged errors, the second of which is pure noise. The abort
   early-return removes both.
3. **`Demo.Web` cannot serve its default page in Production at all.**
   `SpaStaticFilesOptions.RootPath = "ClientApp/dist"` is wrong for Angular 17+ layouts —
   `index.html` lives at `dist/browser/index.html`. Reproducing Defect 2 required copying
   `dist/browser/*` up one level. This is a real demo bug, independent of everything else here.
4. **The dev proxy is a second route to a truncated template.** If a cancellation lands inside
   `CopyProxyHttpResponse`'s mid-copy window, a partial body reaches the `MemoryStream` — the same
   class of corruption as static files' case (b), and a second mechanism behind the report's
   "truncated content" that `GetBuffer()` cannot produce.
5. **Production template ≠ file on disk.** The demo's prod `index.html` is 547 bytes on disk but 456
   in the captured template, because `UseWebMarkupMin` sits inside `next()` and minifies before
   capture. Worth remembering when writing byte-exact assertions.

## Proposed solution (provisional — the solution phase decides)

The reporter's patch, plus the guard from G5:

1. **Defect 1** — decode only the written bytes:
   `Encoding.UTF8.GetString(outputBuffer.GetBuffer(), 0, (int)outputBuffer.Length)`.
   Alternatives to weigh: `ToArray()` (allocates a copy), a `Span` overload, and honouring the
   response's declared charset instead of hard-coding UTF-8.
   **Spike 2 caveat:** `GetString(buffer, 0, Length)` is only correct because the stream is a
   `new MemoryStream()` with `_origin == 0`. For an offset stream (`new MemoryStream(arr, 9, 17, …)`)
   `GetBuffer()` returns the whole array and the "fix" would itself truncate. Harmless today, a
   landmine if the code is ever generalized to a caller-supplied stream — `ToArray()` is the
   origin-safe form and the reason to prefer it may outweigh the copy.
1b. **Defect 1b** — strip a leading U+FEFF from the decoded template (or decode BOM-aware).
   Independent of the length fix; see above.
2. **Defect 2** — **Spike 6's recommendation**: its own explicit early return placed immediately
   after the `finally` and before `canPrerender` is computed, and it should **copy the buffer out**
   rather than bare-`return`:

   ```csharp
   if (context.RequestAborted.IsCancellationRequested)
   {
       await outputBuffer.CopyToAsync(context.Response.Body);
       return;
   }
   ```

   Rationale: in case (a) the copy is provably zero-work (`n == 0` short-circuit), and in case (c)
   the buffer holds the *complete* page that a bare `return` would throw away — so the copy is never
   worse and sometimes better. It also keeps one exit convention: the two existing exits (lines 140,
   163) both copy-then-return, and a third exit that doesn't invites the reader to guess which is
   the bug. Keep it separate from `canPrerender` — that flag answers "is this a usable HTML
   template?", abort answers "should we be doing work at all?", and folding them would also skip
   the check on the `!IsSuccessStatusCode` path.

   ⚠ **Do not thread `context.RequestAborted` into *this* `CopyToAsync`** — `MemoryStream.CopyToAsync`
   front-checks the token and would return `Task.FromCanceled`, throwing an OCE out of the
   middleware on every aborted request. That is a behaviour change, and it is the one place where
   Workstream 3's "thread the token everywhere" instinct is wrong. On the two non-abort copy paths
   the token is fine and is the right thing.
3. **G5 guard** — if the captured template is empty/whitespace, do not call `OnSupplyData` and do
   not call node. Whether this pass-through is silent or logged is a solution-phase decision;
   silently rendering nothing is how this stayed invisible for years, so logging is favoured.
   **Note it does not subsume item 2**: it cannot catch case (b)'s partial template.
4. `IsHtmlContentType(string?)` — nullability fix carried over from #78. Cosmetic, no behaviour
   change (the method already null-checks).

## Risks

| Risk | Mitigation |
|---|---|
| Defect 2 is not reachable in a unit test because the abort behaviour lives in Kestrel, not in middleware | Spike 3 establishes the *contract* (headers set, body empty, no throw); the unit test asserts against that contract with a fake inner pipeline, and Spike 5 confirms it against the real app. Both are stated as such rather than one masquerading as the other. |
| Fixing Defect 1 changes the template every existing consumer's `OnSupplyData` sees | It removes padding that was never part of the response. Any consumer depending on it was already broken. Note in release notes. |
| The abort early-return leaves a response with headers but no body | Irrelevant to a client that has already disconnected; Spike 6 confirms nothing downstream throws. |
| Forcing `RequestAborted` in the demo app is not the same thing as a real browser abort | It is a *confirmation* of the mechanism, not proof of the trigger. Stated honestly in the results; Spike 3 is what ties the mechanism to the real trigger. |
| **(W3)** Threading `RequestAborted` into the node RPC makes cancellations *newly observable*, so code that previously never saw `OperationCanceledException` now can — including `NodeServicesImpl`'s retry path | Spike 8 gates the whole workstream on exactly this. If a cancelled RPC is misread as an unavailable instance, threading the token would spawn retries on every user navigation-away. Answer before implementing. |
| **(W3)** Cancelling the RPC may abandon the .NET side while node keeps rendering | Spike 8. If node cannot be told to stop, the honest outcome may be to thread the token only as far as it does something useful, and document the rest. |
| **(W3)** Changing `INodeServices` overload behaviour is a public-API change | G8. Adding a token to an existing *callsite* is internal; touching the interface is not. Prefer the callsite fix; treat obsoleting overloads as a separate, explicit decision. |

## Success criteria

- Two tests that fail on `master` and pass after the fix, one per defect.
- Spike 5 documented with the actual observed error from the demo app, or a documented reason it
  could not be provoked.
- Every spike question answered in the table above, including the ones whose answer retires a claim
  from the original report.
- Coverage of `…SpaServices.Prerendering` goes up, not down (it is the weakest package at ~4%).
