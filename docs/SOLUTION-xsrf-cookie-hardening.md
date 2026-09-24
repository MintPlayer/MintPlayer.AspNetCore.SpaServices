# Solution: measured behaviour of the XSRF mint, before and after

Evidence for [PRD-Xsrf-Cookie-Hardening.md](./PRD-Xsrf-Cookie-Hardening.md)
([issue #85](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/85)), spikes 1-5.

Everything here was measured against a **real Kestrel host**, not a test harness. The repository's
test suite has no real-server vehicle — no `TestServer`, no `WebApplicationFactory`, no socket —
and, critically, `TestServer` *rethrows* what Kestrel swallows, so the headline defect cannot be
observed in-process at all. These runs are the record.

## Method

A throwaway host in the session scratchpad referencing
`MintPlayer.AspNetCore.SpaServices.Xsrf` directly, listening on `http://localhost:5310` and
`https://localhost:5311`, with:

- a scenario switch — `baseline`, `securealways` (`AntiforgeryOptions.Cookie.SecurePolicy = Always`),
  `throwing` (`InvalidOperationException`), `throwcrypto` (`CryptographicException`), `nulltoken`
  (an `IAntiforgery` returning a null `RequestToken`);
- an upstream middleware registered **before** the mint and another **after** it, each writing a
  marker header from `OnStarting`, to make the LIFO ordering visible;
- `GET /api/ping` (no cache policy) and `GET /api/cached` (sets `Cache-Control: public, max-age=300`
  **eagerly**, the way `[ResponseCache]`, `UseResponseCaching` and prerendering's header
  preservation all leave it).

Each scenario was captured against `master` and again against the fix.

## 1. The cookie on the wire

`baseline`, `GET /api/ping`:

| | Before | After |
|---|---|---|
| HTTPS | `XSRF-TOKEN=…; path=/` | `XSRF-TOKEN=…; path=/; secure; samesite=strict` |
| HTTP | `XSRF-TOKEN=…; path=/` | `XSRF-TOKEN=…; path=/; samesite=strict` |

**No `Secure` even over HTTPS** before the fix, confirming defect 1 directly rather than by
inspection.

Two things visible in the same capture that the issue does not mention:

- `X-Frame-Options: SAMEORIGIN` is present on every response. `SaveCookieTokenAndHeader` writes it,
  and the package inherits it silently. Unchanged by this work (it is additive, not clobbering).
- The framework's own half reads
  `.AspNetCore.Antiforgery.…; path=/; samesite=strict; httponly` — **no `secure`, over HTTPS**.
  That is `AntiforgeryOptions.Cookie.SecurePolicy` defaulting to `None`, it is a larger exposure
  than anything on the JS-readable half, and this package cannot fix it. Now documented in the
  README instead.

## 2. ⚠️ The bare 500 — defect 4 and defect C

`securealways` (`SecurePolicy = Always`), same host, same build, both schemes:

```
HTTP  GET /api/ping ->  HTTP/1.1 500 Internal Server Error
                        Content-Length: 0
                        Date: Thu, 24 Sep 2026 16:53:21 GMT
                        (nothing else - no Server, no Set-Cookie, no Content-Type,
                         neither upstream marker header)

HTTPS GET /api/ping ->  HTTP/1.1 200 OK   + both cookies, both markers
```

The HTTPS request on the same process succeeding is what makes this conclusive: it is the *scheme*,
not a misconfiguration. Behind a TLS-terminating proxy without `UseForwardedHeaders`,
`Request.IsHttps` is `false` on every request, so this is **every request**.

`throwing`, `throwcrypto` and `nulltoken` produce the identical shape. Both predicted exception
paths appear in the host log:

```
fail: Microsoft.AspNetCore.Server.Kestrel[13] … System.InvalidOperationException: …
    at MintPlayer.AspNetCore.SpaServices.Xsrf.Antiforgery.<>c__DisplayClass2_0.<Invoke>b__0(Object state)
    at …HttpProtocol.<FireOnStarting>g__ProcessEvents|241_0(HttpProtocol protocol, Stack`1 events)

fail: … System.ObjectDisposedException: The response has been aborted due to an unhandled
      application exception. ---> System.InvalidOperationException: …
    at …HttpProtocol.InitializeResponseAsync(Int32 firstWriteByteCount)
```

The second is the one the issue does not describe: when the response is written from inside the
pipeline, application code sees an `ObjectDisposedException` *wrapping* the original rather than the
original itself — and cannot recover from it, because `HasStarted` never becomes `true`.

`nulltoken` also confirms the exact exception:

```
System.ArgumentNullException: Value cannot be null. (Parameter 'stringToEscape')
    at System.UriHelper.EscapeString(...)
    at Microsoft.AspNetCore.Http.ResponseCookies.Append(String key, String value, CookieOptions options)
```

**`ParamName` is `stringToEscape`, not `value`** — `ResponseCookies.Append` guards only `options`,
and the throw comes out of `Uri.EscapeDataString`. A test asserting `ParamName == "value"` would
fail.

**After the fix**, every one of these returns `200` with the response intact, no `XSRF-TOKEN`
cookie, and one logged error. The compiler warning `CS8604` on line 20, which the package had been
shipping, is also gone.

## 3. The `Cache-Control` clobber

`baseline`. Note the LIFO relationship, which the first draft of this spike had backwards:

- A writer registered **before** the mint is pushed first, pops **last**, and **wins**.
- A writer registered **after** the mint is pushed last, pops **first**, and **loses**.

Since `UseAntiforgeryGenerator()` is documented to be registered early, almost everything in a real
pipeline is "after" it, and the mint therefore gets the final word.

| Response | Before | After |
|---|---|---|
| `/api/cached` — eager `public, max-age=300` | `no-cache, no-store` | `max-age=300, private` |
| downstream `OnStarting` writer, `public, max-age=60` | `no-cache, no-store` | `max-age=60, private` |
| `/api/ping` — no policy set by the app | `no-cache, no-store` | `no-cache, no-store` *(unchanged)* |

`public` is dropped and `private` forced because the response carries a `Set-Cookie` holding that
user's token; restoring a shared-cacheable directive verbatim would let a CDN serve one user's token
to the next.

Observed live in the `Demo/Xsrf` run, the framework logging the override it performs:

```
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store'
      and 'no-cache' respectively to prevent caching of this response.
```

That warning fires only when a *pre-existing* header is overridden — creating one from nothing is
silent, which is the common case and why this went unnoticed.

## 4. End to end, `Demo/Xsrf`, real browser

`dotnet run --project Demo/Xsrf/XsrfDemo.csproj` — the ASP.NET Core app builds and hosts the Angular
app itself, so no separate `npm install` or `ng serve` step.

Zero configuration; the demo still calls the parameterless `app.UseAntiforgeryGenerator()`.

```
GET https://localhost:5001/WeatherForecast
  Set-Cookie: XSRF-TOKEN=…; path=/; secure; samesite=strict
  Set-Cookie: .AspNetCore.Antiforgery.…; path=/; samesite=strict; httponly

POST https://localhost:5001/WeatherForecast  + X-XSRF-TOKEN  -> 200
POST https://localhost:5001/WeatherForecast  (no header)     -> 400   (control)
```

In a real browser, against the running Angular 21 SPA:

- `document.cookie` exposes `XSRF-TOKEN` and **not** `.AspNetCore.Antiforgery.…` — the hardened
  flags do not stop the SPA reading its half, and the HttpOnly half stays invisible, which is the
  whole point of the pair.
- Clicking *Send a post request* issues `POST /WeatherForecast` → **200**, so Angular's interceptor
  read the `Secure; SameSite=Strict` cookie and the server validated the header.

This is the part curl cannot prove: `Secure` and `SameSite` are enforced by the browser, not the
server.

## 5. Reverse check

With the three defaults reverted (`SameSite=Unspecified`, `SecurePolicy=None`,
`CacheHeaders=NoStore`) and the guards disabled (`catch (Exception ex) when (false)`,
`tokens.RequestToken is null && false`):

**19 of 40 Xsrf tests fail**, spread across all four defect classes — cookie attributes, failure
degradation, error logging, and cache-header preservation. Restoring the implementation returns all
39 to green.

One assertion was strengthened as a result: `Preserves_a_cache_control_set_by_the_application`
originally asserted only that `no-store` survived, which the clobbered value `no-cache, no-store`
also satisfies. It now additionally asserts the *absence* of `no-cache`.

## Coverage

| | Before | After |
|---|---|---|
| Overall line | 80.39% | **81.16%** |
| `…SpaServices.Xsrf` line / branch | 100% / 100% | **100% / 100%** |

The Xsrf assembly was already at 100% before this work — which is the point worth keeping. **Every
defect in issue #85 lived in fully-covered code**, and the suite pinned the cookie exactly as it
was written. Coverage was never going to catch any of it.
