# PRD: `Response.Clear()` destroys every response header set upstream of prerendering

Upstream discussion: [issue #81](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81)
by @Reonekot — *"Discussion: Buildin way to preserve headers from Response object set before calling SSR"*.

## Overview

`ServePrerenderResult` opens with `context.Response.Clear()`
([`SpaPrerenderingExtensions.cs:653`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs)).
That single call resets the status code to 200 and empties the entire response header dictionary, so
**every header written by every middleware upstream of `UseSpaPrerendering` is silently discarded on
exactly the responses that get prerendered** — i.e. every SSR HTML navigation.

The reporter hit it twice: once with a `Cache-Control: no-store` policy for dynamic content, once
when re-enabling `UseHsts()`. They work around it today by capturing `HeaderNames.CacheControl` and
`HeaderNames.StrictTransportSecurity` by hand and restoring them after the render.

This document answers the question raised in the issue thread — *"is there a security consideration
behind clearing the headers, and can we just restore them?"* — and it answers it in two parts:

1. **No.** The `Clear()` is not a security measure. It is inherited verbatim from Microsoft and its
   only real job is to drop the *static file's* `Content-Type` / `Content-Length` / `ETag` before a
   different body is written. **Losing HSTS and CSP on every SSR page is itself the security bug.**
2. **But restoring the headers is not the fix either.** A blanket snapshot-and-restore of
   `Response.Headers` trades one real defect for a strictly worse set: cache poisoning and
   cross-user content disclosure via a restored `ETag`/`Cache-Control`/`Vary`, content-type
   confusion via a restored `Content-Type`, and framing desync via a restored
   `Content-Length`/`Transfer-Encoding`. **The headers that must go and the headers that must stay
   are two disjoint sets, and the framework already has an idiom for separating them.**

## Provenance: this is Microsoft's code, and there was no security rationale

`context.Response.Clear();` is present verbatim at line 238 of the file as first imported in
`6acb640` ("Added SPA prerendering services", 2021-12-02), and the file still carries the .NET
Foundation / Apache-2.0 header at lines 1–2. `git log -L 650,655:` on that line shows it has never
been touched on its own merits — it moved when the surrounding validation and logging were added
(`68487e3`, `367ae1c`, `8b9d7ab`, `de941f9`, `665117d`) and never otherwise.

Nothing in the history, and nothing in `Microsoft.AspNetCore.SpaServices.Extensions`, documents a
reason. It reads as a blunt reset of the static-file response before writing a different body.

### The premise in the issue thread is half right

The issue and the maintainer's reply both assume the `Clear()` is there because the buffer already
holds Angular's `index.html`. **The body half of that is not true**, and the reason matters for the
fix:

`UseSpaPrerendering` swaps `Response.Body` for a `MemoryStream` at
[`SpaPrerenderingExtensions.cs:155-172`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs)
and restores the original stream in the `finally` **before** `ServePrerenderResult` ever runs. The
captured `index.html` bytes live in the `MemoryStream`, which is simply never copied out on the
prerender path. Nothing was ever written to the real response body.

So by the time `Clear()` executes:

- The body is already clean — the swap did that, not `Clear()`.
- `Response.Body` is the *original*, non-seekable Kestrel stream, so `Clear()`'s
  `if (response.Body.CanSeek) response.Body.SetLength(0)` is a **silent no-op**.
- The only thing `Clear()` actually accomplishes is `StatusCode = 200`, `ReasonPhrase = null`, and
  `Headers.Clear()`.

**`Clear()` is being used to solve a header problem, and it is the wrong tool for it.**

## Confirmed mechanism

Per-request flow, verified in this repo:

```
[app pipeline: UseHsts, CSP/security headers, response caching, CORS, ...]   ← headers written here
  └─ UseSpaPrerendering                     SpaPrerenderingExtensions.cs:87
       ├─ Response.OnStarting(OnPrepareResponse)                     :91-95
       ├─ strips If-*/Range/Accept-Encoding request headers        :600-635
       ├─ Response.Body = new MemoryStream()                            :158
       ├─ await next()                                                  :162
       │    └─ SpaDefaultPageMiddleware        Internal/SpaDefaultPageMiddleware.cs:11-65
       │         ├─ Request.Path = "/index.html"                        :19-29
       │         └─ UseStaticFiles → writes index.html INTO THE BUFFER  :34-36
       │              …and its Content-Type/Length/ETag/Last-Modified onto the REAL headers
       ├─ Response.Body = originalResponseStream  (finally)             :167
       ├─ validation gauntlet (content-type, 200-exactly, encoding, …)  :200-340
       ├─ Prerenderer.RenderToString(...)                               :401
       └─ ServePrerenderResult                                          :651
            ├─ context.Response.Clear()   ← EVERY header dies here      :653
            ├─ [redirect branch] Response.Redirect(url, permanent)      :674
            └─ [render branch]  StatusCode? / ContentType="text/html" / WriteAsync(html)  :687-693
```

`ServePrerenderResult` sets **no other header**. There is no restore, and no `HasStarted` check
anywhere in the repo — `Clear()` throws `InvalidOperationException` if the response has started.

The bail-out path `PassThroughAsync` ([:434-450](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs))
does **not** clear; it only reconciles `ContentLength` and copies the buffer. This is why the symptom
is confined to successfully-prerendered responses, which matches the report exactly.

The repo currently **pins this behaviour as intentional**:
`ServePrerenderResultTests.Clears_headers_and_status_written_by_the_inner_middleware`
([`SpaPrerenderingExtensionsTests.cs:376-388`](../MintPlayer.AspNetCore.SpaServices.Tests/Prerendering/SpaPrerenderingExtensionsTests.cs))
asserts `!Headers.ContainsKey("X-Inner")` after the call. That test must be inverted by this work.

### A second clobbering path, out of scope but worth recording

[`Proxying/SpaProxy.cs:173-194`](../MintPlayer.AspNetCore.SpaServices/Proxying/SpaProxy.cs) copies
the dev server's status and all response/content headers wholesale onto `context.Response.Headers`.
Same class of defect, dev-only. See *Out of scope*.

## Why `UseHsts()` does not work — and why `ImprovedHstsMiddleware` does

This is now fully explained, and the explanation generalises.

Microsoft's `HstsMiddleware.Invoke` writes the header **eagerly**, on the way in, and never
re-applies it ([dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/blob/main/src/Middleware/HttpsPolicy/src/HstsMiddleware.cs)):

```csharp
context.Response.Headers.StrictTransportSecurity = _strictTransportSecurityValue;
_logger.AddingHstsHeader();
return _next(context);
```

[`ImprovedHstsMiddleware.cs:92-97`](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/blob/master/Hsts/MintPlayer.AspNetCore.Hsts/ImprovedHstsMiddleware.cs)
does the same thing inside a `Response.OnStarting` callback. **`Response.Clear()` does not remove
`OnStarting` callbacks** — they live on `IHttpResponseFeature`, untouched — so the deferred write
runs at flush time, *after* the `Clear()`, and wins. Everything else about that middleware (options
type, max-age construction, excluded-host matching) is deliberately identical to the framework's.
The Tools repo pins the difference in `ImprovedHstsIntegrationTests.Response_HeaderSurvivesDownstreamHeaderClear`.

That gives a clean predictive rule for the whole ecosystem:

| Producer | Timing | Survives our `Clear()`? |
|---|---|---|
| `HstsMiddleware` — `Strict-Transport-Security` | **eager** | **NO** |
| Idiomatic custom security headers — CSP, `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` | **eager** | **NO** |
| Custom correlation / trace-id middleware | **eager** | **NO** |
| Antiforgery — `Set-Cookie`, `Cache-Control: no-cache, no-store`, `Pragma` | **eager** at token-issue time | **NO** |
| Reporter's own `Cache-Control: no-store` policy | **eager** | **NO** |
| `CorsMiddleware` (non-preflight) | `OnStarting` | yes |
| `CookieAuthenticationHandler` (sliding renewal) | `OnStarting` | yes |
| `SessionMiddleware` | `OnStarting` | yes |
| `ResponseCompressionMiddleware` | applied on first write | yes |
| `MintPlayer.AspNetCore.Hsts` / `.NoSniff` | `OnStarting` | yes |
| `Server`, `Date` | written by Kestrel at flush | yes |

The losses are precisely the **eager** row group, and it is exactly the security-header group. The
survivors are the ones that already had to defer for unrelated reasons.

### Severity

`Strict-Transport-Security` missing from every prerendered HTML navigation is a real
downgrade-to-HTTP exposure, not a cosmetic loss: the browser's HSTS pin is set and refreshed from
document responses, and those are precisely the responses this middleware clears. The same applies
to CSP and `X-Frame-Options` on the SSR page. Affected deployments are those that put security
headers in middleware upstream of `UseSpaPrerendering` — which is the documented, idiomatic
placement.

## The security question, answered: what is safe to restore

The governing rule: **after the body is replaced, the *representation* has changed.** Headers that
describe the transaction stay true; headers that describe the representation become lies, and HTTP
caches and browsers trust those headers over the bytes.

### Group A — must NOT be carried over (they describe the discarded `index.html`)

| Header | Concrete failure if restored |
|---|---|
| `Content-Length` | Stale length truncates the HTML mid-document, or hangs the client; on a keep-alive connection a short read consumes the next response's bytes → **response smuggling / desync** at a shared front-end proxy. |
| `Content-Type` | Restoring the wrong type onto replaced HTML is **content-type confusion → XSS**, made worse because the `Clear()` also removed `X-Content-Type-Options: nosniff`, so the browser resumes MIME sniffing exactly when the declared type is wrong. |
| `Content-Encoding` | `gzip`/`br` declared over an uncompressed body → `ERR_CONTENT_DECODING_FAILED`, blank page. Also risks `gzip, gzip` duplication (see the second-order hazard below). |
| `Content-Range`, `Accept-Ranges` | Declares the full body to be a fragment of a *different* resource; a cache assembling ranges produces a **spliced, corrupted cached object**. |
| `ETag`, `Last-Modified` | **The most dangerous one.** The static `index.html` ETag on a *per-user prerendered* body makes every later `If-None-Match` return `304`, so clients and shared caches serve **the first user's SSR HTML to everyone else** — direct authenticated-content disclosure. |
| `Transfer-Encoding` | Hop-by-hop, owned by the server; never application-set. Restored alongside `Content-Length` it recreates the **CL.TE / TE.CL ambiguity** that request smuggling exploits. |
| `Content-MD5` | Digest of the old bytes. Deprecated (removed in RFC 7231); drop it, as `ResponseCompressionBody` does. |

#### Caching headers are the exception — DECIDED: preserved by default

`Cache-Control`, `Expires`, `Vary` and `Age` were originally placed in Group A on a
fail-safe-restrictive argument: `public, max-age=31536000` is correct for the static `index.html` and
catastrophic on a user-specific SSR body, and a `Vary` that omits the dimensions the SSR body varies
on (`Cookie`, `Authorization`, `Accept-Language`) lets a cache serve one variant to all.

**That argument lost, and the reasoning is worth keeping.** Two facts decided it:

1. **`StaticFileMiddleware` does not set `Cache-Control` at all by default.** It sets `Content-Type`,
   `Content-Length`, `ETag`, `Last-Modified` and `Accept-Ranges`; caching headers arrive only if the
   app configures `OnPrepareResponse` on `StaticFileOptions`. So the value present at the serve point
   is normally the *upstream* one, not the static file's. (Spike 2 confirms against
   `StaticFileContext.ApplyResponseHeaders` rather than taking this on trust.)
2. **Dropping them contradicts this PRD's own success criteria.** Criterion 3 requires an upstream
   `no-store` to survive and criterion 14 requires the defaults alone to satisfy every criterion. A
   default that drops `Cache-Control` makes the reporter's exact use case require configuration —
   which is the bug restated with extra steps.

They are therefore **Group B**. Apps that deliberately set aggressive caching on `index.html` via
`DefaultPageStaticFileOptions` opt out with `DropResponseHeaders`.

**Accepted risk**, recorded so it is not rediscovered as a surprise: an app that *does* configure a
long `max-age` on `index.html` will now publish per-user SSR HTML into shared caches by default, and
the failure is silent and severe. This is a bet that such configuration is rare and close to a
misconfiguration regardless of SSR. The README must warn about it.

### Group B — MUST be preserved, and are currently being destroyed (they describe the transaction)

Safe to carry across the body swap, because none of them says anything about the bytes:

`Strict-Transport-Security`, `Content-Security-Policy` (+ `-Report-Only`), `X-Frame-Options`,
`X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-*-Policy`,
`Set-Cookie`, `Access-Control-*`, correlation/trace ids, and any application-defined `X-` header —
plus `Cache-Control`, `Expires`, `Vary` and `Age` per the decision above.

Remember that Group B is **not a list the implementation contains**. Nothing enumerates what is kept;
these headers survive because they are *not* in the drop set, and so does every header not named
here — including ones from packages that do not exist yet.

**`Strict-Transport-Security` is the headline case of this whole document.** It is set by Microsoft's
own `UseHsts()`, it is destroyed by our `Clear()` on every SSR page, and working around that is the
entire reason
[`MintPlayer.AspNetCore.Hsts`](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/blob/master/Hsts/MintPlayer.AspNetCore.Hsts/ImprovedHstsMiddleware.cs)
exists — that package is a symptom of this bug, not a solution to it. Success criterion 1 is that the
framework's unmodified `UseHsts()` works, so that the workaround package becomes optional.

### Second-order hazard: `OnStarting` survives, so restoring can duplicate

Because `Clear()` cannot remove `OnStarting` callbacks, any restore we perform runs *before* them.
`ResponseCompressionBody` **concatenates** rather than assigns:

```csharp
headers.ContentEncoding = StringValues.Concat(headers.ContentEncoding, compressionProvider.EncodingName);
headers.Vary          = StringValues.Concat(headers.Vary, HeaderNames.AcceptEncoding);
```

Restore a snapshot that already contained `Content-Encoding: gzip` and the deferred callback appends
again → `Content-Encoding: gzip, gzip` (undecodable body) and `Vary: Accept-Encoding, Accept-Encoding`.
The same applies to any `Append`-based custom header middleware. **This defect is inherent to
snapshot-and-restore and absent from the recommended approach**, which never removes the header in
the first place.

### Interaction with response-transforming middleware (WebMarkupMin) — investigated, no change needed

Raised by @PieterjanDeClippel: MintPlayer itself runs **WebMarkupMin**, which strips whitespace and
therefore changes the body length *after* this middleware is done. Investigated, because it is the
sharpest available test of whether the `Response.Body` swap and the header invalidation compose with
a middleware that rewrites the body downstream of us. Three findings, all verified against source:

1. **The body swap composes correctly. No feature is lost.** An earlier concern in this
   investigation — that `Response.Body = originalResponseStream` at `:167` installs a *new*
   `StreamResponseBodyFeature` and thereby orphans a downstream middleware's own feature — is
   **wrong**. `DefaultHttpResponse.Body`'s setter has a revert path:

   ```csharp
   if (otherFeature is StreamResponseBodyFeature streamFeature
       && streamFeature.PriorFeature != null
       && object.ReferenceEquals(value, streamFeature.PriorFeature.Stream))
   {
       // They're reverting the stream back to the prior one. Revert the whole feature.
       _features.Collection.Set(streamFeature.PriorFeature);
       return;
   }
   ```

   WebMarkupMin registers a `BodyWrapperStreamWithResponseBodyFeature` whose `Stream => this`, so
   line 157 captures the wrapper itself and line 167 takes that branch, restoring the original
   feature object with its `BodyWriter`, `StartAsync`, `CompleteAsync` and `SendFileAsync` intact.
   Present since `release/3.1`. **This is why the `IHttpResponseBodyFeature` rewrite stays out of
   scope.**

2. **`Content-Length` is recomputed downstream, so neither the current code nor the fix can get it
   wrong.** `BodyWrapperStreamBase.InternalFinishAsync()` sets
   `responseHeaders[HeaderNames.ContentLength] = processedByteCount.ToString()` on the minify path
   and `responseHeaders.ContentLength = default` when compressing, and clears `Content-MD5` in both.
   `PassThroughAsync`'s pre-minification fix-up at `:434-450` is therefore harmless — overwritten.
   It also independently confirms the Group A rule: WebMarkupMin invalidates exactly the
   representation headers it changes and leaves everything else alone, the same idiom this PRD
   recommends.

3. **Ordering — the analysis predicted "register it before `UseSpaPrerendering`"; the observed
   behaviour is the opposite, and the observation wins.** @PieterjanDeClippel reports that
   `UseWebMarkupMin()` only minifies correctly when registered **inside** the `UseSpa` callback,
   after `UseSpaPrerendering()` (as in `Demo.Web/Startup.cs:123`); anywhere else and minification
   does not happen.

   In that position the registration order is *prerendering → WebMarkupMin → SpaDefaultPage rewrite →
   static files*, so WebMarkupMin sits **inside** the body swap but **outside** static files. It
   therefore minifies **the template** on its way into the `MemoryStream`, and the SSR HTML written
   later by `ServePrerenderResult` bypasses it. The result plausibly still looks fully minified
   because Angular compiles with `preserveWhitespaces: false`, so component markup arrives
   pre-collapsed — the shell is minified by WebMarkupMin and the body by Angular.

   **Hypothesis to test (M6):** the outer position fails today *because of* `Response.Clear()`, and
   removing it lets WebMarkupMin move to a top-level middleware before `UseSpa`. If that holds it is
   both an independent confirmation that the fix is correct and a strictly better end state, because
   outer means the **SSR output** is minified rather than only the template. Falsifiable, and cheap
   to check.

   Either way the header handling is unaffected: in the current inner position WebMarkupMin's
   `Content-Length` describes the minified *template*, so it belongs in the drop set exactly as the
   Group A rule already requires.

This is documentation plus one verification step, not a code change — see M6 and M7.

## Recommended direction: stop clearing, invalidate precisely

The framework's own answer to "I am replacing the body" is not `Clear()`. It is: wrap/replace the
body, leave headers alone, and **null out exactly the representation headers the transformation
invalidates.** `ResponseCompressionBody` is the canonical example:

```csharp
headers.ContentEncoding = StringValues.Concat(headers.ContentEncoding, compressionProvider.EncodingName);
headers.ContentMD5      = default;   // Reset the MD5 because the content changed.
headers.ContentLength   = default;   // Can't know the length after compression
```

`ResponseCachingMiddleware` shims the body stream in a `try` and unshims in a `finally` — the shape
this middleware already uses at `:155-172`.

Applied here, `ServePrerenderResult` becomes: **delete Group A, set what the new body needs, touch
nothing else.** Group B then survives with no snapshot bookkeeping, no allowlist to maintain, no
duplicate-header risk, and — importantly — no need for consumers to enumerate their own headers.

Three candidate shapes, to be decided in the solution phase (Spike 1):

- **O1 — Targeted invalidation (recommended).** Replace `Clear()` with an explicit removal of the
  Group A set, then set status and `Content-Type` as today. Smallest diff; unknown headers default
  to *preserved*, which is the right default for Group B and the wrong one only for an application
  that deliberately set a representation header we failed to enumerate.
- **O2 — Snapshot + allowlist restore.** Keep `Clear()`, restore a configured allowlist. This is the
  reporter's manual workaround promoted into the library. Defaults to *dropped*, needs an options
  surface, and carries the `OnStarting` duplication hazard. Weaker, but it is what the issue proposes,
  so it must be evaluated rather than dismissed.
- **O3 — O1 plus consumer configuration (DECIDED — @PieterjanDeClippel).** O1's built-in drop-set as
  the default, plus two additive options on `SpaPrerenderingOptions` so consumers can adjust it.
  Chosen deliberately over bare O1: the default set cannot be right for every pipeline, and the
  alternative to configuring it is forking the middleware.

  Three constraints keep O3 from collapsing back into O2:

  1. **The base policy stays a drop-list.** The options *adjust* the built-in invalidation set; they
     never replace it and there is no "preserve only these" mode. Unknown headers must keep
     defaulting to preserved — that default is the entire argument for O1 over O2, and a
     consumer-supplied allowlist would reintroduce O2's defaults-to-dropped failure mode.
  2. **Framing headers are not configurable.** `Content-Length`, `Transfer-Encoding` and
     `Content-Range` are correctness, not policy; preserving them is the smuggling/desync hazard in
     Group A. Attempting to preserve one is a **configuration-time error**, thrown from the existing
     `UseSpaPrerendering` guard (see `UseSpaPrerenderingGuardTests`), not a silent per-request
     surprise.
  3. **Minimal surface — but now load-bearing.** Two `ICollection<string>` properties,
     `StringComparer.OrdinalIgnoreCase`. No delegate, no predicate.

     **Revised.** An earlier draft argued the surface could stay small because
     `options.OnPrepareResponse` was a general escape hatch. **`OnPrepareResponse` is now being
     deleted** (see the status section), which removes that fallback: these two collections become
     the *only* configuration route for header behaviour, and anything they cannot express requires
     the consumer to write their own middleware. That raises the bar on getting the defaults right —
     it does not argue for a larger surface, because a consumer's own middleware is a perfectly good
     answer and is where response headers arguably belong anyway.

  Sketch, exact naming to be settled in Spike 1:

  ```csharp
  // Kept even though the built-in set would drop them. Rejected at startup for framing headers.
  public ICollection<string> PreserveResponseHeaders { get; }
  // Dropped in addition to the built-in set.
  public ICollection<string> DropResponseHeaders { get; }
  ```

  The primary intended use of `PreserveResponseHeaders` is the `Cache-Control` / `Vary` / `Expires`
  ambiguity in Spike 2 — an application that knows its caching policy is set upstream can say so,
  which is exactly the reporter's situation and lets the default stay fail-safe-restrictive.

Group A is a **closed, RFC-derived set** — representation metadata is enumerable in a way that
"every header an application might set" is not. That asymmetry is the core argument for O1 over O2.

## Second defect, same root cause: the status code, and the `OnStarting` cascade

Raised by @PieterjanDeClippel. `Clear()` also resets `StatusCode` to 200, and that single fact has
propagated into three layers of workaround — in consumer code, in this library's own routing
package, and in a public API that exists purely to compensate. **This is in scope**: same call, same
line, and fixing the headers without fixing the status would leave the cascade standing.

### The cascade

1. `Clear()` resets the status to 200, so a status assigned in `OnSupplyData` is discarded.
2. Consumers therefore assign it from inside a `Response.OnStarting` callback, which runs after
   `Clear()` and survives. Real example — every 404/403 in
   [`MintPlayer.Web/Services/SpaPrerenderingService.cs`](https://github.com/MintPlayer/MintPlayer/blob/master/MintPlayer.Web/Services/SpaPrerenderingService.cs)
   is wrapped this way:

   ```csharp
   if (person == null)
   {
       context.Response.OnStarting(() =>
       {
           context.Response.StatusCode = StatusCodes.Status404NotFound;
           return Task.CompletedTask;
       });
   }
   ```

3. But a deferred status is **invisible** to the gate at `SpaPrerenderingExtensions.cs:388`, which
   decides whether to prerender by reading `Response.StatusCode`. So `SkipPrerendering()` was
   invented to tell the middleware out-of-band. Its own XML docs
   ([`PrerenderingHttpContextExtensions.cs`](../MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/PrerenderingHttpContextExtensions.cs))
   name the reason exactly: *"a status code assigned from within a `Response.OnStarting` callback is
   not yet visible at that point"*.
4. And this library's own `SpaRouteService.Redirect` (`:110-125`) now stacks all three:

   ```csharp
   context.SkipPrerendering();                              // (3) gate can't see the deferred status
   context.Response.OnStarting(() =>
   {
       context.Response.Redirect(url, permanent: true);     // (2) survive Clear()
       return Task.CompletedTask;                           // permanent:true only because Redirect
   });                                                      // defaults to 302 and would overwrite
   ```

**Every line of that exists to work around `Clear()`.** Preserve the status and the redirect, and it
becomes `context.Response.Redirect(url, permanent: true);` — one line, with the gate seeing the 301
immediately and skipping SSR on its own.

### Why `OnPrepareResponse` is not the answer — and is being deleted

An earlier draft argued that `options.OnPrepareResponse` is a sufficient general escape hatch. For
*headers* it was. For *status* it never could be, and the reason is decisive: `OnPrepareResponse`
runs at flush time, where **there is no context about what was loaded**. Whether the requested person
exists, whether the playlist is private, whether the slug was canonical — all of that is known in
`OnSupplyData` and nowhere else. Pushing the status assignment to a callback that cannot know it is
what forces consumers to capture state into closures and re-check it later.

**DECIDED — @PieterjanDeClippel: delete `OnPrepareResponse` entirely**, along with the
`Response.OnStarting` registration at `SpaPrerenderingExtensions.cs:91-95`. Once headers survive, its
main use evaporates: the Demo's only use of it (`Demo.Web/Startup.cs:116-120`, setting
`Whatever: Oasis`) exists purely because `Clear()` destroyed the header otherwise, and the Demo was
therefore teaching the workaround as the idiom.

What is lost, stated plainly: it ran at flush for **every** request including non-GET and excluded
paths, where `OnSupplyData` never fires. Consumers needing that write their own middleware — which is
where response headers arguably belong. **After this deletion the prerendering middleware registers
no `Response.OnStarting` callback of its own at all.**

### The gate conflates two intents

Preserving the status is necessary but **not sufficient**. The gate at `:388` is:

```csharp
if (!IsSuccessStatusCode(context.Response.StatusCode) || context.IsPrerenderingSkipped())
{
    await PassThroughAsync(context, outputBuffer);
    return;
}
```

If `OnSupplyData` sets 404 directly, this trips and SSR is skipped — the client gets the raw
`index.html` shell with a 404, not a rendered 404 page. That is not what anyone wants, and it is why
the `OnStarting` dance is load-bearing today rather than merely ugly: deferring the status is what
*hides* it from this gate so SSR still happens.

**DECIDED** contract:

| Response state at the gate | Behaviour |
|---|---|
| **3xx with a `Location` header** | Pass through, no SSR. A redirect has no body worth rendering. |
| **304 Not Modified** | Pass through, no SSR, **and no body** — explicit special-case, since a 304 carries no `Location` and would otherwise be prerendered. |
| **204 / 205** | Pass through, **no body**. Same "this status cannot carry a body" rule as 304. |
| **Other 3xx without `Location`** (e.g. 300) | Prerendered. Deliberate: the door stays open for a rendered body on an exotic 3xx. |
| **4xx / 5xx** | **Prerender, and preserve the status.** This is the change. A rendered 404 page returned with a 404 is the whole point. |
| **2xx** | Prerender, preserve the status (200 in practice, given the capture gate already requires exactly 200). |
| `renderResult.StatusCode` set by node | Still wins, unchanged. |

#### Pre-existing bug found while specifying this: `PassThroughAsync` emits a body on a 304

`CanHaveResponseBody` already exists (`SpaPrerenderingExtensions.cs:450-455`) and already knows about
204, 205, 304 and HEAD — but it guards only the `ContentLength` reconciliation. The copy beneath it
is unconditional:

```csharp
await outputBuffer.CopyToAsync(context.Response.Body);
```

So a 304 reaching `PassThroughAsync` sends the entire captured `index.html` as its body. HEAD escapes
only by accident — `StaticFileMiddleware` writes nothing on a HEAD, so the buffer is empty and the
copy is a no-op. For a 304 the buffer is full.

**Fix: gate the copy on `CanHaveResponseBody` as well.** Two lines, reusing the existing helper, no
new concept. This is independent of the header work — any path that reaches `PassThroughAsync` with a
body-less status has the bug today.

### Where a 404 actually comes from — two sources, and node does not report one by default

Worth stating explicitly, because "the render result's status wins" invites the assumption that an
Angular app resolving to its wildcard route reports a 404 to ASP.NET Core. **It does not.**

The channel exists — `RenderToStringResult.StatusCode` and the `{ html, statusCode, redirectUrl,
globals }` protocol — and `ServePrerenderResult` honours it at `:687-689`. But `renderApplication`
has no notion of an HTTP status, and the Demo's boot module returns HTML only
(`Demo.Web/ClientApp/src/main.server.ts:41`):

```ts
return renderPromise.then(html => ({ html }));
```

So `renderResult.StatusCode` is `null` on every render unless the app deliberately sets it. The two
sources of a 404 are complementary:

| Source | Knows about | Mechanism |
|---|---|---|
| **`OnSupplyData` (C#)** | The entity does not exist / is forbidden — it did the lookup | Assign `Response.StatusCode` directly. **This is the path this work fixes.** |
| **Boot module (node)** | The *route* does not exist; Angular's router fell through to its wildcard | Return `{ html, statusCode: 404 }` |

Precedence is narrower than "node wins" suggests: the check is `renderResult.StatusCode.HasValue`,
so node overrides only when the boot module **explicitly** returns a status. Server-set 404 + silent
node keeps the 404. The one arguable case is server 404 + node explicitly 200, where node overrides
an authoritative data lookup — but that requires the app to have said 200 on purpose, so node
continues to win and the rule is documented rather than special-cased.

The effective rule changes from *"node's status, else 200"* (because `Clear()` erased everything
else) to *"node's status, else the server-assigned status, else 200"*. The middle term is new, and is
the whole point of this section.

**Demo work (M7):** extend `main.server.ts` to return a `statusCode` for the wildcard route, so the
client-side path is demonstrated rather than merely possible.

### Established: the status code is owned by `ISpaPrerenderingService`

Decided by @PieterjanDeClippel. The response status for a prerendered page is **the consumer's
responsibility, expressed in `OnSupplyData`**. Concretely, that means the consumer:

- enumerates their routes in `BuildRoutes()`, and
- assigns `404` for any URL that matches no route, plus `404`/`403`/etc. for entities that do not
  exist or are not permitted — exactly the shape of
  [`MintPlayer.Web/Services/SpaPrerenderingService.cs`](https://github.com/MintPlayer/MintPlayer/blob/master/MintPlayer.Web/Services/SpaPrerenderingService.cs).

The middleware does not infer a status from the render, and the node boot module remains an
**optional** override that nothing populates by default. So in practice the status is entirely
controlled by `ISpaPrerenderingService` — which is only workable once it can be assigned directly
rather than through an `OnStarting` callback, which is what this work delivers.

**Documentation obligation (M7):** this makes the C# route table authoritative for status codes, so
it has to be kept in sync with the Angular router. That obligation is currently implicit — the README
must state it, along with the consequence of getting it wrong (a nonexistent route rendering the SPA
shell with a 200, which is an SEO problem rather than a visible one).

### Consequence: `SkipPrerendering()` is kept, but re-justified — DECIDED

Once the status is preserved and the gate reads it correctly, the *documented* reason for
`SkipPrerendering()` / `IsPrerenderingSkipped()` ceases to exist — a redirect set in `OnSupplyData` is
visible to the gate immediately.

**It is kept anyway**, because a second use has nothing to do with this bug and cannot be expressed
any other way: **"render the shell, skip SSR, still return 200."** That is the crawler-only-SSR
pattern (prerender for bots, serve the fast shell to real users) and the node-down kill-switch.
Status-based gating cannot express it, because the status is 200 in every one of those cases.

So the API survives, and:

- its XML docs are **rewritten** — they currently justify it entirely by the `OnStarting` visibility
  problem this work removes, which would leave a public API explained by a defunct mechanism;
- its uses in `SpaRouteService.Redirect` (`:116`, `:133`) are **removed**, because those *were*
  workarounds;
- after this it has **zero callers in the repo**, which is the accepted cost — a public method with
  no in-repo callers tends to accumulate confused ones, so the docs have to carry the weight.

## Success criteria

### Headers

1. `UseHsts()` — the framework's own, unmodified — produces `Strict-Transport-Security` on a
   prerendered response. **The headline acceptance test.**
2. A CSP / `X-Frame-Options` / `Referrer-Policy` header set eagerly upstream survives prerendering.
3. `Cache-Control: no-store` set by upstream middleware survives **with zero configuration**; the
   static file's own `ETag` / `Last-Modified` do not.
4. No `ETag` or `Last-Modified` from `index.html` is ever emitted on a prerendered body.
5. `Content-Length` on the prerendered response either matches the new body exactly or is absent.
6. No duplicated `Content-Encoding` or `Vary` under `UseResponseCompression`.
7. `ImprovedHstsMiddleware` continues to work unchanged, and is documented as an alternative to a
   now-working `UseHsts` rather than a companion — registering both double-writes the header.
8. An unknown, application-defined header survives, because nothing enumerates what is kept.

### Status

9.  A status code set **directly** in `ISpaPrerenderingService.OnSupplyData`, with no `OnStarting`
    callback, reaches the client. **The headline test for the second defect.**
10. A 404 set in `OnSupplyData` is still prerendered, and the SSR "not found" page is returned *with*
    the 404.
11. A 3xx carrying `Location`, and a 304, skip SSR and pass through.
12. 204 / 205 / 304 emit **no body**, on both the gate path and `PassThroughAsync`.
13. `SpaRouteService.Redirect` produces `Location` + 301 with `SkipPrerendering()` and the
    `OnStarting` wrapper both removed.

### Configuration and hygiene

14. **Zero configuration satisfies criteria 1-13.** If any of them needs `PreserveResponseHeaders` to
    pass, the default set is wrong and the option is masking it.
15. `PreserveResponseHeaders` containing a framing header (`Content-Length`, `Transfer-Encoding`,
    `Content-Range`) fails at **startup**, with a message naming the header and the reason.
16. An already-started response fails loudly, with a log line and an exception naming prerendering.
17. No behavioural change on `PassThroughAsync` paths other than criterion 12 and the gate split.

## Decisions taken

Settled with @PieterjanDeClippel during the design interview. Recorded so the reasoning survives into
the PR.

| # | Decision | Reasoning |
|---|---|---|
| 1 | **O3** — built-in drop-set plus two additive options | The drop-set is closed and RFC-derived; "headers an app might set" is not. Unknown headers must default to preserved. |
| 2 | `SkipPrerendering()` **kept**, re-documented; uses removed from `SpaRouteService` | Its stated reason dies with this bug, but "skip SSR on an otherwise-200 response" (crawler-only SSR, node-down kill-switch) cannot be expressed any other way. |
| 3 | `OnPrepareResponse` **deleted**, with its `OnStarting` at `:91-95` | Its only real use was smuggling a header past `Clear()`. Consumers needing every-request behaviour write their own middleware. |
| 4 | Caching headers **preserved** by default | `StaticFileMiddleware` sets no `Cache-Control`; dropping it would make criterion 3 require configuration. Risk accepted and documented. |
| 5 | `SpaRouteService.Redirect` stays unconditional **301** | Correct for slug canonicalisation and SEO; only the comment was wrong. |
| 6 | `SpaProxy` **unchanged**; dev-path test added | It assigns per-key rather than clearing, is dev-only, and forwarding origin headers is what a proxy is for. |
| 7 | `HasStarted` → **guard, log, throw clearly** | Aborting beats a silently truncated page. Believed unreachable since `SupplyData` became `Func<Task>` — so if it fires, something new is wrong and should be loud. |
| 8 | Gate: **3xx with `Location`**, plus an explicit **304** case | Leaves a rendered body possible on an exotic 3xx like 300, while handling 304 correctly. |
| 9 | **204 / 205 / 304 carry no body**, including through `PassThroughAsync` | Closes a pre-existing bug: the buffer copy is currently unconditional. |

### Context worth keeping

- **Why `HasStarted` is unreachable.** Microsoft's `SupplyData` hook was an `Action`, so the pipeline
  continued before it finished and the response could start early — the original source of "the
  response has already started". This fork made it a `Func<Task>` on a scoped service and awaits it,
  eliminating the cause. The guard is belt-and-braces over a root cause already fixed, which is
  exactly why it should be loud rather than forgiving.
- **WebMarkupMin may be able to move to top-level** once `Clear()` is gone (M6). If it can, that is
  independent confirmation the fix is right *and* a better end state, since the outer position
  minifies the SSR output rather than only the template.

## Out of scope / genuinely not being done

- **`SpaProxy` header copying** (`SpaProxy.cs:171-196`). Decision 6. It assigns header-by-header
  rather than clearing, never touches `Strict-Transport-Security` or CSP, and is dev-only. A
  dev-path test is in scope; changing the proxy is not.
- **Rewriting body handling to `IHttpResponseBodyFeature`** instead of `Response.Body`. Ruled out on
  evidence rather than risk appetite: `DefaultHttpResponse.Body`'s `PriorFeature` revert path already
  restores a downstream middleware's feature object intact, so the current swap composes correctly.
- **A general header-transformation API** (predicates, delegates, per-request hooks). Two collections
  plus the consumer's own middleware covers the need, and the deleted `OnPrepareResponse` is a
  cautionary example of a hook outliving its purpose.
