# Solution: the override warning and `SkipXsrfToken`, before and after

Evidence for [PRD-Xsrf-Skip-Token.md](./PRD-Xsrf-Skip-Token.md) and
[PLAN-Xsrf-Skip-Token.md](./PLAN-Xsrf-Skip-Token.md)
([issue #88](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/88)).

Everything here was measured against a **real Kestrel host** over a socket, not `TestServer`. The
same host source was built twice — once against the published package, once against commit
`894e16e` checked out into a detached git worktree — and run on both target frameworks. These runs
are the record.

## Method

A throwaway host in the session scratchpad (`capture88`), one `Program.cs` shared by two project
files that differ only in how they reference the package:

| Variant | Reference | Assembly loaded at run time |
|---|---|---|
| **Before** | `PackageReference MintPlayer.AspNetCore.SpaServices.Xsrf 11.0.0-rc.2` (nuget.org) | `11.0.0-rc.2+c64b014…` |
| **After** | `ProjectReference` to `MintPlayer.AspNetCore.SpaServices.Xsrf` in a detached worktree of commit `894e16e` (`git worktree add --detach <path> 894e16e`, branch `feature/xsrf-skip-token-and-silent-override`) | `11.0.0-rc.3+894e16e3d8e6117e1e3b9af16628192a02e6f874` |

The After assembly's informational version carries the full `894e16e…` hash, so the code under
test is exactly that commit — no uncommitted changes were involved. Each variant has its own
`bin`/`obj`. The host:

- Kestrel on `http://127.0.0.1:0`, `EnvironmentName = "Development"`;
- `services.AddAntiforgery()`; `AddRateLimiter` with `RejectionStatusCode = 429` and a fixed-window
  policy `"one"` (`PermitLimit = 1`, `QueueLimit = 0`, 5-minute window);
- pipeline, in this order: `app.UseAntiforgeryGenerator()` → `app.UseRouting()` →
  `app.UseRateLimiter()` — the generator deliberately **above** an explicit `UseRouting()`, the
  placement the README documents and the one where an endpoint is not yet selected when the
  generator's `Invoke` runs;
- default options (`CacheHeaders = PreservePrivate`), except that After passes a `ShouldIssue`
  predicate to the generator:
  `o.ShouldIssue = ctx => ctx.Request.Path.StartsWithSegments("/media") ? false : ctx.Request.Path.StartsWithSegments("/boom") ? throw new InvalidOperationException("predicate bug") : true;`
  (Before calls `UseAntiforgeryGenerator()` with no options — rc.2 has no `ShouldIssue`);
- every endpoint sets its own headers from the handler (i.e. eagerly, before `OnStarting`):

| Endpoint | Headers the handler sets |
|---|---|
| `/none` | nothing |
| `/public` | `Cache-Control: public, max-age=300` |
| `/private` | `Cache-Control: private, max-age=300` |
| `/nostore` | `Cache-Control: no-store` |
| `/nocache` | `Cache-Control: no-cache` |
| `/pragma-nocache` | `Pragma: no-cache` only |
| `/pragma-public` | `Pragma: public` only |
| `/bad`, `/bad2` | `Cache-Control: max-age="unterminated` |
| `/short` | `Cache-Control: public, max-age=300` + `.ShortCircuit().RequireRateLimiting("one")` |
| `/skip` *(After only)* | `Cache-Control: public, max-age=300` + `.SkipXsrfToken().RequireRateLimiting("one")` |
| `/g/child` *(After only)* | `Cache-Control: public, max-age=300`, in `MapGroup("/g").SkipXsrfToken()` |
| `/g/unskip` *(After only)* | `Cache-Control: public, max-age=300`, in the same skipped group, with its own `.SkipXsrfToken(false)` |
| `/media` | `Cache-Control: public, max-age=300` (After: `ShouldIssue` returns `false`) |
| `/boom` | nothing (After: `ShouldIssue` throws) |

A client `HttpClient` with `UseCookies = false` (so every request is cookie-less, as a first visit
is) requested, in order: `/none`, `/public` **twice**, `/private`, `/nostore`, `/nocache`,
`/pragma-nocache`, `/pragma-public`, `/bad`, `/bad2`, `/short` **twice**, in After `/skip`
**twice**, `/g/child` and `/g/unskip`, and finally `/media` and `/boom` in both variants. Per request it recorded status, `Cache-Control`, `Pragma`, the number of
`Set-Cookie` headers and their names, and `X-Frame-Options`. A recording `ILoggerProvider` (minimum
level `Warning`, all other providers cleared) captured every Warning-or-higher entry as category,
EventId and the first 100 characters of the message, cleared between requests.

Runtimes, from `dotnet --list-runtimes` and confirmed in-process:

| TFM | `RuntimeInformation.FrameworkDescription` | `Microsoft.AspNetCore.App` |
|---|---|---|
| net10.0 | `.NET 10.0.12` | `10.0.12` (`+95017c711e6a…`) |
| net11.0 | `.NET 11.0.0-rc.1.26425.128` | `11.0.0-rc.1.26425.128` (`+3551975be087…`) |

## 1. The override warning

Every cookie-issuing response, both variants, both TFMs, carried `Set-Cookie: 2`
(`.AspNetCore.Antiforgery.…` and `XSRF-TOKEN`) and `X-Frame-Options: SAMEORIGIN`. All returned 200.
(`/media` is in section 5: Before it is one more `[8]` warning, After it is not minted at all.)

| Endpoint | Before: `DefaultAntiforgery[8]` | After: `DefaultAntiforgery[8]` | `Cache-Control` (before = after) | `Pragma` (before = after) |
|---|---|---|---|---|
| `/none` | no | no | `no-store, no-cache` | `no-cache` |
| `/public` (1st) | **yes** | no | `max-age=300, private` | — |
| `/public` (2nd) | **yes** | no | `max-age=300, private` | — |
| `/private` | **yes** | no | `max-age=300, private` | — |
| `/nostore` | **yes** | no | `no-store, private` | — |
| `/nocache` | **yes** | no | `no-cache, private` | — |
| `/pragma-nocache` | no | no | `no-store, no-cache` | `no-cache` |
| `/pragma-public` | **yes** | no | `no-store, no-cache` | `public` |
| `/short` (1st, 2nd) | **yes**, both | no | `max-age=300, private` | — |

**Headers on the wire are byte-for-byte identical before and after for every endpoint in this
table.** Only the log changed: on these endpoints 8 `[8]` warnings per Before run (9 counting
`/media`), 0 per After run, on both TFMs, and no other Warning+ entry replaced them. The only
Warning+ entries in an After run are the `/bad` warning (section 2) and the `/boom` Error
(section 5) — 2 in total per TFM.

The Before warning, as captured (truncated at 100 characters by the recorder):

```
[Warning] Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery [8] The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'n
```

Observations from the same capture:

- **The warning was a false alarm.** It fires on `/public`, `/private`, `/nostore` and `/nocache`,
  yet the final header is the application's own directives (with `public` turned into `private`).
  The package had already restored them by the time the response left; the framework reported an
  override that did not survive.
- **It fires per request, not once.** Both `/public` requests logged it.
- **It fires on `/private` and `/nostore`** — responses that were already non-shared-cacheable,
  where nothing of substance was being overridden at all.
- **`/pragma-nocache` was silent even Before**, one of two endpoints with a pre-set header (with `/bad`, section 2) that did not
  trigger EventId 8 — a bare `Pragma: no-cache` is already what the framework writes, and it adds
  `Cache-Control: no-store, no-cache` alongside it.
- **`/pragma-public` keeps `Pragma: public` in both variants**, alongside
  `Cache-Control: no-store, no-cache`. That is unchanged by this work; `Cache-Control` takes
  precedence over `Pragma` for HTTP/1.1 caches.
- `/none` is the common case and was silent both times: the framework creates the headers from
  nothing, which it does not report.

## 2. Unparseable `Cache-Control`

`/bad` and then `/bad2`, both setting `Cache-Control: max-age="unterminated`:

| | Before | After |
|---|---|---|
| `/bad` — `Cache-Control` / `Pragma` | `no-store, no-cache` / `no-cache` | `no-store, no-cache` / `no-cache` |
| `/bad` — log | **nothing** | one `MintPlayer.AspNetCore.SpaServices.Xsrf.Antiforgery` Warning |
| `/bad2` — `Cache-Control` / `Pragma` | `no-store, no-cache` / `no-cache` | `no-store, no-cache` / `no-cache` |
| `/bad2` — log | nothing | nothing (once per process) |

Before, the application's policy was replaced and **nobody said so** — `DefaultAntiforgery` does not
log EventId 8 for a value it cannot parse, so this was the one override that went unreported. After,
the same headers go out and the package says why, once:

```
[Warning] MintPlayer.AspNetCore.SpaServices.Xsrf.Antiforgery [0] The application set Cache-Control: max-age="unterminated on /bad, which cannot be parsed. A response
```

The package warning carries no EventId (`0`).

## 3. `SkipXsrfToken` vs `ShortCircuit`, generator above `UseRouting()`

All three endpoints set `Cache-Control: public, max-age=300`; `/short` and `/skip` share the
`"one"` rate-limit policy (one permit per window).

| Request | Status | `Cache-Control` | `Set-Cookie` | `X-Frame-Options` | Warning+ log |
|---|---|---|---|---|---|
| Before `/short` 1st | 200 | `max-age=300, private` | 2 | `SAMEORIGIN` | `DefaultAntiforgery[8]` |
| Before `/short` 2nd | **200** | `max-age=300, private` | 2 | `SAMEORIGIN` | `DefaultAntiforgery[8]` |
| After `/short` 1st | 200 | `max-age=300, private` | 2 | `SAMEORIGIN` | none |
| After `/short` 2nd | **200** | `max-age=300, private` | 2 | `SAMEORIGIN` | none |
| After `/skip` 1st | 200 | **`public, max-age=300`** | **0** | — | none |
| After `/skip` 2nd | **429** | — | 0 | — | none |
| After `/g/child` | 200 | **`public, max-age=300`** | **0** | — | none |

Raw After lines:

```
== GET /short -> 200 | Cache-Control: max-age=300, private | Pragma: - | Set-Cookie: 2 [.AspNetCore.Antiforgery.fPLy_NQwse4,XSRF-TOKEN] | X-Frame-Options: SAMEORIGIN
== GET /short -> 200 | Cache-Control: max-age=300, private | Pragma: - | Set-Cookie: 2 [.AspNetCore.Antiforgery.fPLy_NQwse4,XSRF-TOKEN] | X-Frame-Options: SAMEORIGIN
== GET /skip -> 200 | Cache-Control: public, max-age=300 | Pragma: - | Set-Cookie: 0 [] | X-Frame-Options: -
== GET /skip -> 429 | Cache-Control: - | Pragma: - | Set-Cookie: 0 [] | X-Frame-Options: -
== GET /g/child -> 200 | Cache-Control: public, max-age=300 | Pragma: - | Set-Cookie: 0 [] | X-Frame-Options: -
```

What this shows:

- **`ShortCircuit()` was never a workaround with the generator above `UseRouting()`.** The
  generator's `OnStarting` callback is registered before routing runs, so a short-circuited endpoint
  still gets both cookies and still loses `public`. And it skips everything after routing: the
  second `/short` is **200**, the rate-limit policy on it silently ignored.
- **`SkipXsrfToken()` keeps the response public and cookie-free while the rest of the pipeline
  still runs**: the second `/skip` is **429** from the rate limiter. The skip is decided in the
  `OnStarting` callback, by which time routing has selected the endpoint, so it works from the
  documented position above `UseRouting()`.
- **A group convention reaches its children**: `/g/child` carries no metadata of its own and is
  skipped through `MapGroup("/g").SkipXsrfToken()`.
- **A skipped response has no `X-Frame-Options`**, as the `SkipXsrfTokenAttribute` remarks warn:
  that header is a side effect of the antiforgery mint, and nothing else in this host sets it.
- The 429 carries no `Cache-Control` and no cookie — the rejection is written by the rate limiter,
  and no token was issued for it either.

## 4. Un-skipping inside a skipped group

`/g/unskip` sits in the same `MapGroup("/g").SkipXsrfToken()` group as `/g/child`, with its own
`.SkipXsrfToken(false)`. Both set `Cache-Control: public, max-age=300`.

| Request (After) | Status | `Cache-Control` | `Set-Cookie` | `X-Frame-Options` | Warning+ log |
|---|---|---|---|---|---|
| `/g/child` | 200 | `public, max-age=300` | 0 | — | none |
| `/g/unskip` | 200 | `max-age=300, private` | **2** | `SAMEORIGIN` | none |

```
== GET /g/unskip -> 200 | Cache-Control: max-age=300, private | Pragma: - | Set-Cookie: 2 [.AspNetCore.Antiforgery.fPLy_NQwse4,XSRF-TOKEN] | X-Frame-Options: SAMEORIGIN
```

The endpoint's own `Skip = false` wins over the group's `Skip = true` (last metadata wins): the
token is minted, `public` is downgraded to `private` exactly as on `/public`, and no `[8]` warning
is logged.

## 5. `XsrfOptions.ShouldIssue`, including a throwing predicate

| Request | Status | `Cache-Control` | `Pragma` | `Set-Cookie` | `X-Frame-Options` | Warning+ log |
|---|---|---|---|---|---|---|
| Before `/media` | 200 | `max-age=300, private` | — | 2 | `SAMEORIGIN` | `DefaultAntiforgery[8]` |
| After `/media` (predicate → `false`) | 200 | **`public, max-age=300`** | — | **0** | — | none |
| Before `/boom` | 200 | `no-store, no-cache` | `no-cache` | 2 | `SAMEORIGIN` | none |
| After `/boom` (predicate throws) | 200 | `no-store, no-cache` | `no-cache` | **2** | `SAMEORIGIN` | **one Error** |

Raw After lines:

```
== GET /media -> 200 | Cache-Control: public, max-age=300 | Pragma: - | Set-Cookie: 0 [] | X-Frame-Options: -
   LOG (none)
== GET /boom -> 200 | Cache-Control: no-store, no-cache | Pragma: no-cache | Set-Cookie: 2 [.AspNetCore.Antiforgery.fPLy_NQwse4,XSRF-TOKEN] | X-Frame-Options: SAMEORIGIN
   LOG [Error] MintPlayer.AspNetCore.SpaServices.Xsrf.Antiforgery [0] XsrfOptions.ShouldIssue threw, so the XSRF-TOKEN cookie was issued on this response as if it had ret
```

What this shows:

- **A `false` from `ShouldIssue` leaves the response untouched**: `/media` keeps the application's
  `public, max-age=300`, gets no cookie and no `X-Frame-Options` — the same outcome as
  `SkipXsrfToken()`, decided by path instead of endpoint metadata.
- **A throwing predicate fails open**: `/boom` is minted exactly as Before (both cookies, the
  framework's `no-store, no-cache` / `no-cache`, `SAMEORIGIN`), the request still returns 200, and
  exactly one Error is logged by `MintPlayer.AspNetCore.SpaServices.Xsrf.Antiforgery`, EventId `0`.
- Before, `/media` is just another `/public`: minted, downgraded to `private`, and one more false
  `[8]` warning.

## 6. Runtimes

The net10.0 and net11.0 captures are **identical** for each variant — every status, header value,
cookie name and count, and log line — once the version banner is excluded (`diff` of the remaining
lines is empty for both Before and After). No TFM-specific behaviour was observed.

## 7. Reverse check

Against the rc.2 `AntiforgeryMiddleware.cs` (new tests kept, implementation reverted), 21 of the
79 Xsrf tests fail on both net10.0 and net11.0; with the implementation, 79/79 pass on both.
*(Measured at `2255b1c`, before the review changes in `894e16e` added further tests.)*

Full suite: 659 tests per TFM, all green on net10.0 and net11.0; Xsrf assembly 100% line and 100%
branch coverage on both TFMs.
