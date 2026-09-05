# Solution: the prerender response-header drop-set and its options contract

Decision record for **M4a** of [PLAN-Prerendering-Response-Headers.md](./PLAN-Prerendering-Response-Headers.md)
([PRD](./PRD-Prerendering-Response-Headers.md), [issue #81](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81)).

Settles the two things left open after the design interview: **which headers `ServePrerenderResult`
removes**, and **the exact shape of the consumer-facing options**. Everything else was decided in the
PRD's *Decisions taken* table.

## 1. The rule

`Response.Clear()` is replaced by a targeted removal. The governing rule, restated so the list below
can be checked against it:

> When the prerendered HTML replaces the captured `index.html`, the **representation** changes but
> the **transaction** does not. Remove every header that describes the old representation. Leave
> everything else — including headers this library has never heard of.

The list is therefore closed and derivable: it is *representation metadata* as defined by RFC 9110
§8, plus the framing headers. It is not a list of "headers we happen to know about".

## 2. The drop-set

Removed unconditionally by `ServePrerenderResult` before the new body is written.

| Header | Why it describes the discarded representation |
|---|---|
| `Content-Length` | Byte count of `index.html`. Wrong for the SSR body; a stale value truncates or desyncs the response. |
| `Content-Type` | Media type and charset of the captured file. `ServePrerenderResult` sets its own. |
| `Content-Encoding` | Encoding the captured body was in. The SSR body is written uncompressed; anything downstream re-applies its own. |
| `Content-Language` | Language of the captured entity, not necessarily of the rendered page. |
| `Content-Range` | Byte range of a *different* entity. Never valid on a replaced full body. |
| `Content-Location` | Alternate URI of the captured representation. |
| `Content-MD5` | Digest of the old bytes. Deprecated (removed in RFC 7231); `ResponseCompressionBody` also clears it. |
| `Accept-Ranges` | Advertises range support for the static file; the SSR body is generated per request and cannot honour ranges consistently. |
| `ETag` | **The most dangerous.** The static file's validator on a per-user SSR body means later `If-None-Match` requests get a 304 and shared caches serve one user's page to everyone. |
| `Last-Modified` | Same class as `ETag`: a validator for the wrong entity. |
| `Transfer-Encoding` | Hop-by-hop framing, owned by the server. Application code must never carry it across a body swap. |

Eleven headers. **Deliberately excluded** from the drop-set, each for a stated reason:

| Header | Why it is *not* dropped |
|---|---|
| `Cache-Control`, `Expires`, `Vary`, `Age` | PRD decision 4. `StaticFileMiddleware` sets no `Cache-Control` by default, so the value present is normally the upstream one, and dropping it would make success criterion 3 require configuration. `DropResponseHeaders` is the opt-out for apps that set caching on `index.html`. |
| `Pragma` | Legacy `Cache-Control` companion; only ever set deliberately by an app, never by the static-file path. |
| `Set-Cookie`, `Access-Control-*`, `Strict-Transport-Security`, CSP, `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-*` | Transaction-scoped, not representation-scoped. These are the headers the whole bug is about. |
| `Server`, `Date` | Written by Kestrel at flush; not ours to manage. |
| Everything else | Not enumerated, therefore preserved. **This is the point of the design** — a drop-set fails open, an allowlist fails closed. |

### Superset check against `StaticFileMiddleware`

The drop-set must be a superset of everything `StaticFileMiddleware` can set, or the old
representation leaks. *(Filled in from the source audit — see §5.)*

## 3. The options contract

```csharp
/// Headers kept even though the built-in drop-set would remove them.
public ISet<string> PreserveResponseHeaders { get; }

/// Headers removed in addition to the built-in drop-set.
public ISet<string> DropResponseHeaders { get; }

/// The built-in drop-set, exposed so consumers can see what they are adjusting.
public static IReadOnlyCollection<string> DefaultDroppedResponseHeaders { get; }
```

Decisions, each with its reason:

- **`ISet<string>`, not `ICollection<string>` or `string[]`.** Set semantics are what these are:
  membership tests, no meaningful order, duplicates harmless. `ExcludeUrls` on the same options
  object is a settable `string[]`, so this is a deliberate inconsistency — chosen because a settable
  array can be null and needs ordinal-ignore-case comparison written at every use site, whereas a
  get-only set carries its comparer and can never be null.
- **Get-only, initialised to `new HashSet<string>(StringComparer.OrdinalIgnoreCase)`.** Header names
  are case-insensitive per RFC 9110 §5.1, so `"cache-control"` and `"Cache-Control"` must be the same
  entry. Get-only removes the null branch entirely.
- **Both collections are additive adjustments; neither replaces the built-in set.** There is no
  "preserve only these" mode. Unknown headers must keep defaulting to preserved — that is the whole
  argument for this design over a snapshot-and-restore allowlist.
- **A name in both collections is a startup error**, not a precedence rule. A configuration that
  contradicts itself is a bug in the consumer's code, and silently picking a winner hides it.
- **Framing headers cannot be preserved.** `Content-Length`, `Transfer-Encoding` and `Content-Range`
  in `PreserveResponseHeaders` throw at startup. These are correctness, not policy: preserving them
  is the response-smuggling / desync hazard from the PRD, and no consumer has a legitimate reason to.
  Note they *may* appear in `DropResponseHeaders` — redundant, but harmless and not worth rejecting.

### Validation

Lives in the existing `UseSpaPrerendering` guard, placed **after** the `BootModulePath` check and
**before** `spaBuilder.ApplicationBuilder` is dereferenced — matching how
`UseSpaPrerenderingGuardTests.UnusableSpaBuilder` proves a guard runs without entering the middleware
body.

Both failures throw `InvalidOperationException`, and the message carries the *why*, not just the
name:

- *"`Content-Length` cannot be preserved across prerendering: it describes the captured template, and
  emitting it alongside a different body corrupts the response framing. Remove it from
  `PreserveResponseHeaders`."*
- *"`X-Foo` appears in both `PreserveResponseHeaders` and `DropResponseHeaders`. Remove it from one."*

## 4. What this replaces

`context.Response.Clear()` at `SpaPrerenderingExtensions.cs:653` becomes a removal loop over
`DefaultDroppedResponseHeaders ∪ DropResponseHeaders \ PreserveResponseHeaders`, computed **once at
startup** rather than per request — the sets are immutable after configuration, so the effective drop
list is a single precomputed collection captured in the middleware closure alongside
`excludePathStrings`.

The status code is no longer reset to 200; see the status contract for what replaces that.

## 5. Source audit: what `StaticFileMiddleware` actually sets

Audited against `dotnet/aspnetcore` `release/10.0`. `ApplyResponseHeadersAsync`
(`src/Middleware/StaticFiles/src/StaticFileContext.cs:243-279`) is the **only** method that writes
response headers, apart from two `ContentRange`/`ContentLength` writes in `SendRangeAsync`
(`:373`, `:380-381`).

| Header | Set when |
|---|---|
| `Content-Type` | status < 400, when a content type was resolved |
| `Last-Modified` | status < 400, always |
| `ETag` | status < 400, always |
| `Accept-Ranges` | status < 400, always, literal `"bytes"` |
| `Content-Length` | 200 → full length; 206 → range length |
| `Content-Range` | 206 (computed range) and 416 (`*/{length}`) |

**Six headers. The drop-set of eleven is a strict superset.** ✅

`ComprehendRequestHeaders` and its four `Compute*` helpers (`:143-241`) only *read* request headers —
they write no response header. `SetCompressionMode` (`:408-411`) sets the `IHttpsCompressionFeature`
flag, not `Content-Encoding`.

### `Cache-Control` — confirmed absent, which is what decision 4 rested on

`Cache-Control`, `Expires`, `Vary`, `Age` and `Pragma` appear nowhere in `StaticFileContext.cs`,
`StaticFileMiddleware.cs`, `StaticFileOptions.cs` or `SharedOptions.cs`. `StaticFileOptions` has no
caching-header default; the hooks are no-ops that are not even invoked unless the app replaces them:

```csharp
internal static readonly Action<StaticFileResponseContext> _defaultOnPrepareResponse = _ => { };
internal static readonly Func<StaticFileResponseContext, Task> _defaultOnPrepareResponseAsync = _ => Task.CompletedTask;
```

`Microsoft.AspNetCore.SpaServices.Extensions` adds nothing either: `SpaDefaultPageMiddleware`
forwards `options.DefaultPageStaticFileOptions ?? new StaticFileOptions()` unchanged, and
`SpaOptions.DefaultPageStaticFileOptions` defaults to null.

**So `StaticFileOptions.OnPrepareResponse` is the sole route by which any other header reaches a
static-file response — and it can set anything.** That is the one open-ended hole in this analysis,
and it is exactly the configuration decision 4 accepted as a documented risk.

### Two findings that do not change the drop-set

1. **`Location` on a trailing-slash 301.** `DefaultFilesMiddleware` and `DirectoryBrowserMiddleware`
   emit `Location` + 301 via `Helpers.RedirectToPathWithSlash`. Not a concern here: a 301 carries
   `Location`, so the gate passes it through and `ServePrerenderResult` never runs.
2. **`StaticFileMiddleware` calls `Response.Clear()` itself** in the `FileNotFoundException` catch of
   `SendAsync` (`:331`) before falling through to `next`. That is framework code running *inside* our
   `next()`, so it wipes upstream headers on that path no matter what this work does. Reachable only
   when a file disappears between stat and open, after which the SPA terminal middleware throws
   anyway. Recorded as a known limitation, not something we can fix from here.

### Status-path variations, for the pass-through tests

| Path | Headers emitted |
|---|---|
| 200 GET | Content-Type, Last-Modified, ETag, Accept-Ranges, Content-Length |
| 200 HEAD | identical — `Content-Length` is still the full length, with no body written |
| 206 | + Content-Range; Content-Length is the *range* length |
| 304 | Content-Type, Last-Modified, ETag, Accept-Ranges. **No Content-Length** |
| 412 | status only |
| 416 | **Content-Range only** |

## 6. Rejected alternatives

- **O1 (no options).** Rejected in the design interview: the default cannot be right for every
  pipeline, and the alternative to configuring it is forking the middleware.
- **O2 (snapshot + allowlist restore)** — the shape proposed in issue #81. Rejected for three
  independent reasons: it fails closed on any header nobody enumerated, which is the present bug
  restated; restoring a header that an `OnStarting` callback later *appends* to produces duplicates
  (`Content-Encoding: gzip, gzip`), because `Clear()` cannot remove those callbacks; and it keeps the
  `Clear()` whose status-code reset is the second defect.
- **A predicate or delegate hook** (`Func<string, bool>`, or a header-transform callback). Rejected:
  two sets plus the consumer's own middleware covers the need, and the deleted `OnPrepareResponse` is
  a cautionary example of a hook outliving its purpose.
