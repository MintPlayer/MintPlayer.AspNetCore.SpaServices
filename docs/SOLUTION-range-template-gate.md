# SOLUTION: the template-validity gate (issue #80 and its class)

Scope of this document: `canPrerender`, `RemoveConditionalRequestHeaders`, the capture decode, and
the completeness guard that replaces `string.IsNullOrWhiteSpace`. **Out of scope and owned
elsewhere:** `PassThroughAsync`'s `ContentLength` reconciliation (the shipped HEAD regression) and
the request-method gate. Where a decision here touches those, it is flagged as a coordination note,
never as a change.

Input: `docs/PRD-Prerendering-Range-Requests.md` (mechanism, reproduction, breadth survey) and
`docs/PRD-Prerendering-Aborted-Requests.md` + `docs/SOLUTION-defect2-abort.md` for the established
contract. Code read: `MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs`
(current), `Tests/Prerendering/SpaPrerenderingMiddlewareTests.cs`,
`Tests/Prerendering/SpaPrerenderingExtensionsTests.cs`, `Tests/Prerendering/RangeReproTests.cs`,
`Internals/LoggerFinder.cs`.

---

## 0. The shape of the decision

`canPrerender` today asks two questions — 2xx, and `text/html` — and infers *"this capture is the
complete representation the client would otherwise have received."* Neither question is about
completeness. The gate is therefore restructured as one predicate with a name that states the
inference it is actually making:

```
IsCompleteRepresentation(response)  ==  status is exactly 200
                                     && no Content-Range response header
                                     && Content-Encoding absent or identity
                                     && (ContentLength not declared || captured == declared)
```

plus, after the decode, two content-level checks (valid UTF-8; no U+0000) and one *warning-only*
structural observation. `IsHtmlContentType` is unchanged. `IsSuccessStatusCode` **stays** — it is
still what the post-`OnSupplyData` re-check at line 225 needs (see §2).

Everything above is app-agnostic: no check depends on what the SPA's markup looks like.

---

## 1. Strip `Range`, narrow the status check, or both?

**Both. The PRD's defence-in-depth argument is confirmed, and it is stronger than the PRD states.**

- The strip is the *causal* fix: it removes the request-side input that makes downstream produce a
  partial representation at all. It is exactly why `If-None-Match` is already stripped — we strip
  the headers whose whole purpose is to make the server return something other than the full
  entity. `Range` is the last member of that family still present, and the omission is plainly an
  oversight, not a decision.
- The status/framing narrowing is the *containment* fix, and it is not hypothetical. `SpaProxy`
  forwards every request header except `Connection` and `CopyProxyHttpResponse` copies status and
  `Content-Type` verbatim, so a dev server (or any upstream behind a consumer's own proxy
  middleware) that answers 206 for its index document reproduces #80 through a path our header
  surgery never touches. A consumer middleware inside `next()` can also produce a 206 that no
  request header of ours explains.

Neither alone is sufficient: the strip cannot reach a producer that never looks at our request, and
the narrowing alone would leave us *asking* for a 206 on every bot request and then throwing the
answer away — turning a 500 into a needless pass-through of a one-byte body when a full page was
available and wanted.

**The PRD's finding on `If-Range` is confirmed and is the decisive detail.** `ComputeIfRange` is the
only code path in `StaticFileContext` that can clear `IsRangeRequest` after `ComputeRange` has
parsed it. By stripping `If-Range` and leaving `Range`, we removed the only cancel and kept the
trigger — our own header surgery makes the 206 *more* likely than it would be without us. Stripping
`Range` restores the invariant the method was written for; there is no case for restoring `If-Range`
instead (that would make behaviour depend on the client's etag, i.e. non-deterministic).

### Restore `Range` after the capture? **No.**

The `Accept-Encoding` restore is not symmetry — it is **functionally load-bearing**, and reading the
code makes the asymmetry principled rather than sloppy:

- `Accept-Encoding` is restored inside the `finally` (lines 139-142), i.e. *before*
  `ServePrerenderResult` writes the prerendered body to the real `Response.Body`. Any compression
  middleware registered **upstream** of `UseSpaPrerendering` makes its decision at first write —
  which, on the prerendered path, happens after `next()` has returned. If the header were still
  missing at that moment, prerendered responses would silently never compress while every other
  response did. The restore is required for correctness of the outbound response.
- `Range` has no such consumer. Nothing between the capture and the wire reads it: we do not, and
  must not, produce a 206 ourselves (see the PRD's non-goal — a per-request generated body has no
  stable byte range). Restoring it would restore a value that nothing can act on except, in the
  worst case, a consumer's own middleware upstream of us deciding to slice our prerendered output.

So: strip `Range` alongside the conditional headers, in `RemoveConditionalRequestHeaders`, and do
not restore it — the same treatment the four `If-*` headers already get, which keeps that method
internally consistent.

**Trade-off, stated:** a client that asked for bytes gets a whole page. That is permitted (a server
MAY ignore `Range`), it is the only coherent option for generated output, and it is invisible to
browsers (Chromium sends no `Range` on document navigation — verified in the PRD). The cost lands on
CDNs and scanners, which handle a 200 for a range request routinely. **Release-note it.** A second,
smaller cost: the code comment on `RemoveConditionalRequestHeaders` must be rewritten — it currently
explains only the 304, and the `Range` line needs the `If-Range` reasoning above recorded next to
it, or the next reader will "restore symmetry" by re-adding `If-Range`.

---

## 2. If the status check narrows, to what?

**`200` exactly, *and* reject a `Content-Range` response header. Both, for different failure
modes.**

`200` exactly is the invariant: of the 2xx codes, only 200 promises "the body is the complete
representation of the target resource". Enumerating the rest confirms there is nothing to keep:
201/202/203 have no producer on this path and none of them promises the representation of the
requested resource (203 explicitly disclaims it), 204/205 must have no body, 206 is the defect, 207
is WebDAV, 226 needs delta encoding. Nothing legitimate is lost.

The `Content-Range` check is not redundant with it — it catches the one shape the status check
cannot: **a consumer's middleware that rewrote the status while leaving the framing headers behind.**
`UseStatusCodePagesWithReExecute` and `UseExceptionHandler` both rewrite status inside `next()`, and
`ExceptionHandlerOptions.StatusCodeSelector` is an unconstrained `Func<Exception,int>`; a
re-executed action returning `Ok()` produces exactly `200 + stale Content-Range`. It also costs one
header lookup.

**Which fails more safely with an unknown consumer middleware?** `200`-exactly, decisively. "2xx
minus `Content-Range`" fails *open* on every status whose semantics we have not thought about — it
accepts a 206 whose producer forgot `Content-Range` (a proxy that copies status but not headers:
precisely the dev-proxy shape), and it accepts future/vendor 2xx codes by default. `200`-exactly
fails *closed*: an unknown status means pass-through, which is always a valid outcome because the
captured bytes are exactly what the client would have received. Using both gives the closed default
plus one extra rejection for a status we would otherwise trust.

**The rationale is framing, not truncation.** The PRD's point stands and must be recorded in the
code comment, because it is counter-intuitive enough to be "fixed" later: `bytes=0-` yields a 206
carrying the *complete* document, so "206 implies truncated" is false — and rejecting it is still
correct, because `Content-Range` and the range unit describe a byte range of a specific entity, and
prerendering replaces the entity. There is no honest way to emit `Content-Range: bytes 0-546/547`
over a 25 KB rendered page. A 206 is therefore un-prerenderable *whatever* its body contains, and
that is a statement about framing.

### Not narrowed: the post-`OnSupplyData` re-check (line 225)

`IsSuccessStatusCode` stays there, unchanged. The two checks ask different questions: the pre-gate
asks *"is this capture the complete representation?"*, the post-check asks *"does the prerendering
service still want us to render a body, or has it redirected / errored?"*. The template has already
been validated by then; narrowing the second check to 200 would newly reject a service that
deliberately sets, say, a 201 or 202 on its prerendered response, for no gain. `IsSuccessStatusCode`
and `IsSuccessStatusCodeTests` therefore both survive.

---

## 3. The layered completeness check

### 3.1 Framing invariants — **include**

Exactly four, all decisive and app-agnostic:

| # | Invariant | What it closes | False-positive surface |
|---|---|---|---|
| F1 | status == 200 | A2 (206 from any producer), 416 and every other status | none (§2) |
| F2 | no `Content-Range` | A2 via a status-rewriting middleware | a middleware that sets `Content-Range` on a genuinely complete 200 — a protocol error either way |
| F3 | `Content-Encoding` absent or `identity` (case-insensitive, single value) | B1 (`MapStaticAssets` Brotli variants, or any consumer that encodes inside `next()`) | a consumer explicitly declaring `identity` is accepted; anything else is genuinely undecodable by `Encoding.UTF8.GetString` |
| F4 | `ContentLength` not declared, **or** captured bytes == declared bytes | B5, and mid-copy truncation with no cancelled token | the hard call — §3.1.1 |

F3 deserves one note: the `Accept-Encoding` strip is described in the current code as a precaution,
and it has never had a check behind it. F3 is that check. It is also the cheapest of the four — a
header read — and it is the only thing standing between a compressed capture and
`Encoding.UTF8.GetString` on Brotli bytes.

**F1-F3 are what actually close #80.** F4 closes neither #80 nor anything the abort check already
covers; it is included on its own narrow merits, argued below.

Method (GET) belongs in this set conceptually, and is deliberately absent here: it is the other
agent's decision, and F1-F4 must not silently pre-empt it. Whoever lands the method gate should add
it to the same predicate.

#### 3.1.1 F4: declared-vs-captured. The hard call, reconciled

`SOLUTION-defect2-abort.md` §2 **rejected** this comparison as control flow, in three arguments.
Taking them in turn, honestly:

> "It has legitimate false positives inside `next()`. `UseWebMarkupMin` turns a 547-byte file into a
> 456-byte template. Any response-transforming middleware legitimately makes buffer length ≠ the
> `ContentLength` some inner component set."

**This premise does not survive scrutiny, and it is the reason the rejection can be reversed.** The
missing observation is that *the same mismatch would be fatal outside our capture*. Kestrel enforces
declared-vs-written in both directions: too many bytes throws on the write, too few throws at
response completion (`Response Content-Length mismatch: too few bytes written` — the very exception
the previous PRD chased in its §"Follow-up found by the verification"). A transforming middleware
that shrank the body while leaving a stale larger `Content-Length` would therefore break **every**
response it touched on every non-prerendered route in the application. `UseWebMarkupMin` is widely
deployed and does not do this; whatever it does with the header (update it to the minified length,
or remove it and let the response go chunked), the post-transform state is necessarily *consistent*,
because an inconsistent one is unshippable. Our `MemoryStream` is the only thing in the pipeline that
would have masked it.

So `ContentLength.HasValue && captured != declared`, observed at the moment `next()` returns, means
one of exactly two things:

1. the capture is short of what downstream believed it wrote — truncation, or bytes still sitting in
   an unflushed `PipeWriter` (B5); or
2. the application has a latent framing bug that Kestrel would reject on any route we are not
   capturing.

Neither is a template. In both cases skipping prerendering is right.

> "It is absent exactly where it would be needed. Chunked responses carry no `ContentLength` at all."

**Correct, and unchanged.** F4 is additive, never a substitute. It is silent on the whole chunked /
dev-proxy family, which is precisely why the `HasValue` guard is part of the invariant rather than a
regrettable limitation: no declared length, no claim, no rejection.

> "The case it would catch is already covered causally — truncation comes from an abort, and the
> abort check catches it at the source."

**Mostly true, and it is why F4's unique value is narrow.** The abort check (`RequestAborted`) fires
first and covers mid-copy truncation on a cancelled token. F4's residual, non-overlapping coverage
is: truncation with **no** cancelled token — an upstream socket error inside the dev proxy's copy, a
downstream stream that stops early, and B5's `PipeWriter` bytes that never reach the buffer. Real,
reachable, and today entirely undetected.

**Decision: include F4, in one direction only, and pin it with a test.**

- Fire only when `ContentLength.HasValue && outputBuffer.Length < context.Response.ContentLength`.
  A *short* capture is the failure mode; captured-greater-than-declared is unreachable through
  anything Kestrel would have accepted, and rejecting it buys nothing while adding a second way to
  be wrong. Asymmetric on purpose.
- Log it at **Warning**, with both numbers (§5). This is what makes the reversal defensible: the
  previous rejection's real objection was not "the signal is wrong", it was *"a false positive means
  prerendering silently stops working, which is far worse and much harder to diagnose."* That
  objection was correct against an **unlogged** check. A Warning naming the path, the declared
  length and the captured length turns a silent regression into a one-line diagnosis, and the
  middleware now has logging infrastructure it did not have when that call was made.
- Pin the transformer contract with a committed test (§6, test 17): a wrapper inside `next()` that
  shrinks the body **and** updates `ContentLength` must still prerender. If the premise above is
  ever wrong for a real consumer, that test is where the counter-example lands, and F4 is one `&&`
  clause to remove.

**Should the comparison move out of `PassThroughAsync`? No.** A3's "the signal is computed and used
backwards" is resolved by *adding* the conclusion at the gate, not by relocating the arithmetic. The
reconciliation in `PassThroughAsync` still has a job on the abort path (defining out of existence
the synthetic-token mismatch documented in the previous PRD), and it applies on paths the gate never
sees. Both may read `ContentLength`; that is not duplication worth removing.

**Coordination note for the other agent (not a change requested here):** on an F4 rejection the
capture is passed through, and `PassThroughAsync` will then reconcile the declared length *down* to
the captured bytes — right for a genuine truncation (we cannot invent bytes we do not have), and the
exact mechanism behind the shipped HEAD regression. Whatever makes reconciliation conditional for
HEAD must not accidentally disable it for the truncation case; a HEAD-shaped condition (no body
expected) rather than a "buffer is empty" condition keeps both correct.

### 3.2 Decode integrity — **include, but not as the PRD framed it**

The PRD offers "reject U+FFFD / U+0000, or decode with `throwOnInvalidBytes: true`". Both forms have
a defect, and there is a third that has neither.

- `throwOnInvalidBytes` reintroduces exactly what the previous PRD deliberately avoided: a throw on
  the request path, from a partial UTF-8 sequence in a truncated capture. It also destroys the
  diagnostic — we cannot log the template head of a string we failed to produce.
- Rejecting the *decoded* U+FFFD conflates two different things: invalid bytes (a corrupt capture)
  and a legitimate template that happens to contain a literal replacement character in valid UTF-8.
  Rare in an `index.html`, but it is a false positive that silently disables prerendering, and we
  have just spent §3.1.1 arguing that those are the expensive kind.

**Decision: keep the lenient decode exactly as it is, and validate the bytes separately.**

1. Before decoding, `System.Text.Unicode.Utf8.IsValid(buffer)` on the captured span (available on
   `net10.0`, which both projects target; no allocation, no exception, no fallback plumbing). Invalid
   → reject at Warning. This is precise: it distinguishes "these bytes are not UTF-8" from "this
   text contains U+FFFD", which no post-decode inspection can do.
2. After decoding, reject if the template contains **U+0000**. This is B2 verbatim: UTF-16-encoded
   markup is *valid* UTF-8 byte-wise for the ASCII half and decodes to `<\0!\0d\0…`, and
   `char.IsWhiteSpace('\0')` is `false`, so it sails past the existing empty guard into NG05104. A
   NUL is also not representable in a conforming HTML document (the HTML parser replaces it), so
   there is no legitimate template to break. Note this check would *also* have caught the
   pre-#79 `GetBuffer()` padding defect — cheap corroboration that it discriminates real corruption.
3. Do **not** reject on U+FFFD. Pinned by a test (§6, test 15) so it is not "tightened" later.

The lenient decode therefore stays lenient, and keeps its stated reason (a truncated capture must
not throw); the strictness moves to a check that can report instead of throw. Between them, F3 and
these two close B1 and B2 without knowing anything about the app.

### 3.3 Structural HTML as a warning — **include, at two levels**

Include, and keep it strictly non-rejecting. The PRD's honesty about the limit is right: domino
normalizes a bare `<app-root></app-root>` into a full document and the render succeeds, so a fragment
template is a legitimate — if unusual — deployment, and a hard rejection would turn a working
application into 500s for no proven gain. The measurements settle it independently: `bytes=0-99`
*starts with* `<!doctype html><html lang="en">` and would pass any structural test, so this layer
would not have caught #80 even if it were a rejection. It is a diagnostic, and should be honest about
being one.

**What is checked:** the decoded template contains `<html` **and** `</html>`, both ordinal
case-insensitive. Nothing more — no doctype requirement (legitimate templates omit it, and a
doctype check demonstrably fails on `bytes=0-99` anyway), no `<body>`, no counting.

**Level:** `Warning` **once per pipeline**, plus `Debug` on every occurrence. The reasoning:

- Warning-every-time is unacceptable — a fragment deployment would emit a warning per request
  forever, and the previous PRD's HEAD lesson (a Warning with a benign cause is worse than no
  Warning) applies verbatim.
- Debug-only is what the reporter already had, and it failed: they had to hand-build their own
  logging because nothing was visible at default levels. That is the problem being solved.
- Once-per-pipeline gets the diagnostic in front of anyone reading logs at default level on the
  first affected request, then goes quiet. The flag is a closure-scoped `int` with
  `Interlocked.Exchange` next to the existing captured `logger`/`options` — no static state, scoped
  to the `UseSpaPrerendering` call, which is the right lifetime.

**Message content:** path, template length in characters, and the **first 200 characters** of the
template with CR/LF collapsed to spaces so it stays one log line. That is precisely the diagnostic
the reporter had to build by hand, and it is the only place in this design where template content is
logged (see §5 on why that matters).

### 3.4 Guessing the root element — **exclude**

Confirmed, without reservation. `<app-` prefix matching, "contains a hyphenated custom element",
"has a child of `<body>`" all fail on real applications and each one converts a working deployment
into a 500. `Contains("<our-app")` is correct **for the reporter** and correctly lives in *their*
`OnSupplyData`, where it already does its job.

Also excluded from this PR: the opt-in escape hatch the PRD mentions (a configured root selector or
a template predicate on `SpaPrerenderingOptions`). It is the only sound route to a strong check, and
it should be recorded as such — but no consumer has asked for it, #80 is closed by F1-F3, and it is
new public API surface that would need documenting and supporting. This is a genuine non-need, not
work deferred to keep the diff small. It is the answer if B3 (§7) is ever reported for real.

---

## 4. What the response should be on rejection

**Confirmed: pass the capture through unchanged, on every rejection path. No new behaviour.**

For a 206 this is not a compromise, it is the right answer: the client asked for a byte range and
receives exactly the bytes downstream produced, with its own `Content-Range` and `Content-Length`
intact and mutually consistent. We are declining to *prerender*; we are not declining to *serve*.
The same holds for 416, for a compressed capture (the client's own `Accept-Encoding` asked for it —
note our strip only applies to the sub-request, and F3 rejections can only arise from a producer
that encoded regardless), and for a truncated capture (passing the bytes on is strictly better than
substituting a page we cannot build).

**On the leaked `Content-Range` / `Content-Length: 1`:** they stop leaking, and not because we strip
them. Today those headers ride onto the 500 because prerendering *proceeds*, NG05104 throws from
line 239, and `UseExceptionHandler` re-enters a pipeline whose response headers were set by the 206.
Once the gate rejects, there is no throw, no error page, and no 500 — the headers stay on the 206
they correctly describe. **Do not strip `Content-Range` on the pass-through path**: on a legitimate
206 from a consumer's middleware, removing it would corrupt a correctly framed response and is the
one way this fix could make things worse.

One consequence worth recording in the code comment: after the `Range` strip, a 206 reaching the
gate is *anomalous* — nothing in the request we forwarded asked for one. That is the justification
for logging framing rejections at Warning rather than Debug (§5), and it is a genuine behaviour
difference from today, where the 206 is expected because we asked for it.

---

## 5. Logging

Design constraints taken from the two PRDs: the reporter had to build their own logging to diagnose
#80 at all; the empty-template Warning currently fires on every benign HEAD and is therefore already
teaching consumers to ignore this category; and template content in logs is not free.

**One message template per rejection cause**, not a shared template with a `{Reason}` field — the
fields differ per cause, and log-based alerting keys on the template. Every line names the path and
says "Skipping prerendering", matching the two existing lines.

| Cause | Level | Fields |
|---|---|---|
| Partial / non-200 framing (F1, F2) | **Warning** | `{Path}`, `{Method}`, `{StatusCode}`, `{ContentRange}` |
| `Content-Encoding` (F3) | **Warning** | `{Path}`, `{ContentEncoding}` |
| Short capture (F4) | **Warning** | `{Path}`, `{CapturedBytes}`, `{DeclaredBytes}` |
| Invalid UTF-8 / NUL in template (§3.2) | **Warning** | `{Path}`, `{CapturedBytes}`, `{ContentType}`, `{TemplateHead}` |
| No `<html>`/`</html>` (§3.3, non-rejecting) | **Warning** once, then `Debug` | `{Path}`, `{TemplateLength}`, `{TemplateHead}` |
| Aborted request (existing) | `Debug` | unchanged |
| Empty template (existing) | `Warning` | unchanged — but see the coordination note below |

**Warning is the right level for all five new lines**, and this is a considered position given the
HEAD lesson. Each has no benign cause *after* this fix: we no longer send `Range`, we no longer send
`Accept-Encoding`, a declared length that does not match what was written is a framing bug
somewhere, and a template that is not valid UTF-8 or contains NUL is corrupt. Prerendering silently
not happening is the failure mode consumers cannot diagnose, so each of these must be visible at
default level. The one non-rejecting line is the one that gets the once-only treatment, precisely
because it is the one with a benign cause.

**Field-by-field justification** (rather than logging everything everywhere):

- `{Path}` — every line. Without it the operator cannot tell which route stopped prerendering.
- `{Method}` — framing line only. It is what separates "a bot sent `Range`" from "a monitoring probe
  sent HEAD", and it is part of the framing set even though the method gate itself is not this
  agent's decision. Not on the other lines, where it does not discriminate anything.
- `{StatusCode}` — framing line only. It is the rejected value.
- `{ContentRange}` — framing line only, and it is **the** field that would have identified #80.
  `bytes 0-0/547` says in one token: this is range traffic, this is the slice, and the entity is 547
  bytes. Nothing else in the response says that.
- `{ContentEncoding}` / `{CapturedBytes}` / `{DeclaredBytes}` / `{ContentType}` — each only on the
  line whose rejection they explain. `ContentType` earns its place on the decode line specifically
  because B2 is a charset problem and the declared charset is the first thing to look at.
- `{TemplateHead}` (first 200 chars, newlines collapsed) — **only** on the two content-level lines.
  Deliberately *not* on the framing lines: there the headers fully explain the rejection, and an
  `index.html` can carry CSP nonces, inline bootstrap configuration and build metadata that has no
  business being duplicated into a log store on every bot request.
- **User-Agent — rejected**, against the PRD's suggestion. Method + status + `Content-Range` already
  identifies #80 in one reading; UA adds request-identifying data to a library's log output for no
  additional diagnostic, and any consumer who wants it already has request logging.

**What would have identified #80 in one reading.** This line, at default level:

> `Skipping prerendering of /person: the captured response is a partial representation
> (GET, status 206, Content-Range "bytes 0-0/547").`

**Log category.** `LoggerFinder.GetOrCreateLogger(applicationBuilder, nameof(UseSpaPrerendering))`
produces the category `"UseSpaPrerendering"`. That is not filterable through the conventional
`Logging:LogLevel:<namespace>` configuration, so a consumer cannot turn this middleware's Debug lines
on without turning on everything. **Recommendation: change the category to the full type name**
(`MintPlayer.AspNetCore.SpaServices.Prerendering.SpaPrerenderingExtensions`). It is a one-line change
in the `UseSpaPrerendering` callsite, it makes every line in the table above configurable, and it is
a small breaking change for anyone filtering on the old string — so it needs a release-note line.
Flagged as a recommendation rather than folded in silently, since it also affects the two existing
lines the other agent is touching.

**Coordination note (not a decision here):** the existing empty-template Warning and its "There is
no known benign cause" comment are demonstrably wrong for HEAD. That belongs to the method-gate
agent. If a method gate lands, the empty guard's Warning becomes correct again and the comment can
stay; if it does not, the level must drop. Either way, the empty guard itself stays — it is the only
check that covers a chunked empty capture, where F4 is silent.

---

## 6. Tests

### What to keep from `Tests/Prerendering/RangeReproTests.cs`

The scratch file is deleted; five of its seven cases move into the committed suite with inverted
assertions, and two are dropped:

| Scratch case | Disposition |
|---|---|
| `Repro_1` (`bytes=0-0` → `originalHtml == "<"`) | **Keep, inverted** → test 1 |
| `Repro_2` (`Range` survives the strip) | **Keep, inverted** → test 5 |
| `Repro_3` (`Range` + full 200 still prerenders) | **Keep as-is** → test 7 |
| `Repro_4` (`bytes=0-99`) | **Keep, inverted** → test 2, and it is load-bearing |
| `Repro_5` (`bytes=100-200`) | **Keep, inverted** → test 3 |
| `Repro_6` (multi-range → 200) | **Keep as a control** → test 9 |
| `Repro_7` (416) | **Keep as a control** → test 8 |

Its `PartialContent(slice, from, to, total)` helper is exactly the `StaticFileMiddleware` 206
contract (status, the file's own `Content-Type`, slice-length `ContentLength`, `Content-Range`,
`Accept-Ranges`) and should move onto `PrerenderingHarness` next to `AbortedStaticFile`. Note it sets
`ContentLength` to the *slice* length, faithfully — which is why **F4 does not catch #80** and F1/F2
must.

### Committed suite

New class `RangeAndTemplateValidityTests` in
`Tests/Prerendering/SpaPrerenderingMiddlewareTests.cs`, alongside `AbortedRequestTests`. Every test
below fails on the current branch unless marked *(control — passes today)*.

**Framing: partial content**

1. `Does_not_prerender_a_single_byte_partial_response` — `PartialContent(bytes[..1], 0, 0, 547)`,
   request carries `bytes=0-0`. Asserts `WasCalled == false`; client body is that one byte; status
   still 206; `Content-Range` still `bytes 0-0/547`; `ContentLength == 1`. The reported symptom,
   inverted, plus §4's promise that the framing headers are left intact.
2. `Does_not_prerender_a_markup_shaped_partial_slice` — `bytes=0-99` of a 20 KB page whose first 100
   bytes are `<!doctype html><html lang="en">…`. **Load-bearing:** the test body must first
   `Assert.StartsWith("<!doctype html><html lang=\"en\">", slice)` so the file itself records *why*
   a plausibility check cannot work, then assert `WasCalled == false`. Without that first assertion
   the test looks like a duplicate of test 1 and invites someone to delete it.
3. `Does_not_prerender_a_mid_document_partial_slice` — `bytes=100-200`, no `<html` in the slice at
   all. Same assertions.
4. `Does_not_prerender_a_two_hundred_that_still_carries_a_content_range` — 200, `text/html`,
   complete body, stale `Content-Range`. Pins F2 independently of F1, i.e. the status-rewriting
   middleware shape. Fails today.

**Request-side strip**

5. `Strips_the_range_header_before_the_capture` — inner pipeline records
   `Request.Headers[Range]`; asserts it is absent, and that `If-Range` is absent too (the existing
   behaviour that made the range un-cancellable).
6. `Does_not_restore_the_range_header_after_the_capture` — asserts `Range` is still absent on the
   context after the pipeline completes, while `Accept-Encoding` **is** restored. One test, both
   halves, because the asymmetry is the decision and a test that only checks one half would be
   "corrected" by the next reader.
7. `Prerenders_normally_when_a_range_request_is_answered_with_a_full_two_hundred` *(control — passes
   today)* — `Range` present, downstream answers 200 with the whole page: still prerenders, full
   template.

**Controls that a status-check change must not break**

8. `Does_not_prerender_an_unsatisfiable_range_response` *(control)* — 416 + `Content-Range: bytes
   */547`. Passes today via `IsSuccessStatusCode`; must keep passing via F1. Its point is that a
   future "let's accept 2xx again" change cannot quietly start accepting it.
9. `Prerenders_a_multi_range_request_that_downstream_ignored` *(control)* — `bytes=0-0,2-2`, 200 with
   the whole file.
10. `Prerenders_a_malformed_range_request_that_downstream_ignored` *(control)* — `Range: bananas=1-2`
    (and a second case, `bytes=abc`), 200 with the whole file. ASP.NET Core ignores both, and the
    PRD's note that neither `RangeHelper.ParseRange` nor `RangeHeaderValue` validates the *unit* is
    why `items=0-0` is worth one `[Theory]` row here rather than a comment.

**Content-Encoding (B1)**

11. `Does_not_prerender_a_capture_with_a_content_encoding` — 200, `text/html`,
    `Content-Encoding: br`, body = arbitrary non-UTF-8 bytes. `WasCalled == false`; body passed
    through byte-for-byte.
12. `Prerenders_a_capture_that_declares_identity_encoding` — `Content-Encoding: identity` (and a
    `[Theory]` row for `IDENTITY`) still prerenders. Guards F3 against over-firing.

**Decode integrity (B2, §3.2)**

13. `Does_not_prerender_a_capture_that_is_not_valid_utf8` — body = a truncated multi-byte sequence
    plus real markup, or raw Brotli bytes with no `Content-Encoding`. `WasCalled == false`.
14. `Does_not_prerender_a_utf16_encoded_template` — B2 verbatim: `Encoding.Unicode.GetBytes(index)`
    with `Content-Type: text/html; charset=utf-16`. Documents in a comment that
    `char.IsWhiteSpace('\0')` is `false`, which is why the existing empty guard does not catch it.
15. `Prerenders_a_template_containing_a_literal_replacement_character` — valid UTF-8 containing
    U+FFFD as content. **Must prerender.** This is the test that pins the choice of `Utf8.IsValid`
    over "reject U+FFFD"; without it, someone simplifies the check and introduces the false positive.

**Declared-vs-captured (F4, §3.1.1)**

16. `Does_not_prerender_a_capture_shorter_than_its_declared_content_length` — 200, `text/html`,
    `ContentLength = 20000`, 8192 bytes written, **not** aborted. `WasCalled == false`. Fails today,
    and it is the deliberate reversal of `SOLUTION-defect2-abort.md` §2 / that document's test 7.
17. `Prerenders_a_response_a_transforming_middleware_shrank` — **the regression guard for the hard
    call.** An inner wrapper inside `next()` sets `ContentLength = 547`, writes a 547-byte page,
    then minifies to 456 bytes *and updates `ContentLength` to 456* (the `UseWebMarkupMin` shape,
    modelled on the previous PRD's collateral finding #5). Must still prerender, with the 456-byte
    template. If F4 ever has to be removed, this is the test that will have failed.
18. `Prerenders_a_short_chunked_capture_with_no_declared_length` — 200, `text/html`, no
    `ContentLength`, short body. Must prerender: F4 makes no claim without a declared length.
19. `Prerenders_a_capture_longer_than_its_declared_content_length` — pins F4's one-directionality.
    Arguably over-specified; keep it, because "reject on any mismatch" is the obvious simplification
    and this records that it was declined.

**Structural warning (§3.3)**

20. `Prerenders_a_fragment_template_without_an_html_element` — body is `<app-root></app-root>`.
    Must **prerender** (`WasCalled == true`), and must log. Requires the harness addition below.
21. `Warns_once_about_a_fragment_template_and_then_stays_quiet` — two requests through the *same*
    pipeline; exactly one Warning, and a Debug on both. Pins the closure-scoped flag, including that
    it is per-pipeline rather than per-process (a second `Run` gets its own Warning).

**Existing tests affected**

- `Still_prerenders_a_partially_captured_template` (line ~506) **keeps passing unchanged** —
  `HtmlPageInChunks` sets `ContentLength` to the sum of the chunks it actually writes, so
  captured == declared and F4 is silent. But its comment (*"the reason the abort check is not
  redundant with the empty-template guard"*) and the entry it pins in
  `SOLUTION-defect2-abort.md` §2 are now **half wrong**: with a *declared* length, F4 catches that
  case too. Rewrite the comment to say the abort check remains the only cover for a **chunked**
  partial, and cross-reference test 16. Leaving it as-is is the single most likely way this decision
  gets silently reverted later.
- `IsSuccessStatusCodeTests` — unchanged (§2: the helper survives for the post-`OnSupplyData` check).
- `RemoveConditionalRequestHeadersTests.Removes_every_conditional_header` — add `Range` to the
  header set it writes and asserts on; the reflection harness (`SpaPrerenderingReflection`) already
  exposes the method. Add a matching `[Theory]` unit test for the new
  `IsCompleteRepresentation`-style helper through the same reflection harness if it is factored as a
  private static, covering: 200 accepted; 200 + `Content-Range` rejected; 206 rejected; 416 rejected;
  201/202/203/204/226 rejected; `identity` accepted; `br` rejected.

**Harness additions required** (`PrerenderingHarness`, all small):

- `PartialContent(byte[] slice, long from, long to, long total)` — moved from the scratch file.
- A log-capturing `ILoggerFactory` registered in the harness's *application* services. Today none is
  registered, so `LoggerFinder` returns `NullLogger` and **no log assertion is possible anywhere in
  the suite**. Tests 20-21 need it, and it retroactively makes the two existing log lines from #79
  assertable. Smallest form: an `ILoggerProvider` collecting `(LogLevel, EventId, string)` into a
  list, exposed on `Result`.
- `configureContext` already exists and is sufficient for the request-header cases.

---

## 7. Which Tier-B findings this closes, and which it does not

| # | Closed? | By what, and what remains |
|---|---|---|
| **A2** (206) | **Yes, twice** | `Range` strip removes the cause; F1/F2 reject any 206 from a producer we cannot influence (dev proxy, consumer middleware, `FileResult` with `enableRangeProcessing`, `MapStaticAssets`). |
| **A4** (nothing on the response is inspected) | **Mostly** | `Content-Range`, `Content-Encoding` and the length comparison are now read. `Request.Method` and `Transfer-Encoding` remain unread — method is the other agent's; `Transfer-Encoding` is not observable at this layer under Kestrel and needs nothing. |
| **B1** (`Content-Encoding`) | **Yes** | F3 rejects it outright, and §3.2's `Utf8.IsValid` catches a compressed capture that arrives with no header at all. The `Accept-Encoding` strip finally has a check behind it. |
| **B2** (non-UTF-8 charset) | **Yes, in practice** | UTF-16 is caught by the NUL check; any single-byte legacy encoding with a non-ASCII byte is caught by `Utf8.IsValid`. A pure-ASCII page mislabelled `windows-1252` is byte-identical to UTF-8 and needs no catching. The decode stays UTF-8-only by design (honouring the charset on the read side alone would turn visible mojibake into silently wrong bytes). |
| **B4** (`Response.Clear()` inside the capture) | **Partly, and unchanged by this fix** | `Clear()` wipes headers, so `ContentType` becomes null and `canPrerender` already rejects — that half is covered incidentally. The half where a middleware clears *and* re-sets `Content-Type` is B3 by another name. The stale-tail consequence is already nil thanks to the `TryGetBuffer` decode; the NUL check is a second net under it. |
| **B5** (unflushed `PipeWriter` writes) | **Partly** | Caught by F4 whenever a `ContentLength` was declared — which is the static-file and most MVC shapes. Not caught on a chunked response, where nothing at this layer can distinguish a short capture from a short page. Document as a known limitation. |
| **A5** (deferred status codes) | **No, and out of scope** | `SkipPrerendering()` remains the opt-in answer; a third-party status set inside `OnStarting` is undetectable by construction. Unchanged from the previous PRD's decision. |
| **A1 / A3-HEAD / the method gate** | **Not mine** | F1-F4 are written so the method invariant slots into the same predicate. |
| **B3** (an error page captured as the template) | **NO. Plainly not caught.** | It is 200, `text/html`, no `Content-Range`, no `Content-Encoding`, `ContentLength` consistent, valid UTF-8, no NUL, and it contains `<html>`/`</html>`. It passes every check in this document — by construction, because it *is* a complete representation; the thing that is wrong with it is that it is the representation of a *different* resource, and no app-agnostic check can see that. It is also the one case in the whole survey that looks perfectly healthy in a log. |

**B3 is therefore a documented known limitation, and the documentation is part of this work:**

1. A note in the Prerendering README: do not register `UseExceptionHandler` or
   `UseStatusCodePagesWithReExecute` **inside** the `UseSpa`/prerendering callback — the error page
   lands inside the capture and becomes the SSR template. Worth naming the demo's own commented-out
   `spa.ApplicationBuilder.UseResponseCaching()` as the invitation to that placement, since it is in
   this repo and reads as an endorsement.
2. A note that a consumer who needs a hard guarantee should assert on the template in their own
   `OnSupplyData` — which is what the reporter's `Contains("<our-app")` does, and it is the correct
   home for an app-specific check.
3. The sound in-library fix, if it is ever reported for real, is the opt-in predicate declined in
   §3.4 — recorded there so the next person does not re-derive it.

---

## Summary of decisions

| # | Decision |
|---|---|
| 1 | **Both.** Strip `Range` in `RemoveConditionalRequestHeaders` (the cause) **and** narrow the gate (containment against the dev proxy and consumer middleware). **Do not restore `Range`** — the `Accept-Encoding` restore is functionally required for upstream compression at prerender-write time; `Range` has no consumer after the capture. Record the `If-Range` reasoning in the comment so the strip is not "symmetrised" away. |
| 2 | **Status exactly 200, *and* reject a `Content-Range` response header.** 200-exactly fails closed on unknown statuses; the `Content-Range` check covers a status-rewriting middleware. Rationale is *framing*, not truncation — a `bytes=0-` 206 carries the whole document and is still un-prerenderable. `IsSuccessStatusCode` stays, for the post-`OnSupplyData` re-check only. |
| 3 | **Framing invariants F1-F4 included** (200; no `Content-Range`; `Content-Encoding` absent/`identity`; declared length, if present, not greater than captured). **F4 reverses `SOLUTION-defect2-abort.md` §2** on the ground that its central premise — a transformer legitimately leaving a stale `Content-Length` — cannot survive Kestrel's own mismatch enforcement on every non-captured route; the reversal is conditioned on `HasValue`, one-directional, logged at Warning, and pinned by a transformer test. The comparison is **added** at the gate, **not moved** out of `PassThroughAsync`. |
| 4 | **Decode integrity included, reframed:** keep the lenient decode; validate bytes with `Utf8.IsValid` before decoding and reject U+0000 after. Reject neither U+FFFD-as-content nor by throwing. |
| 5 | **Structural check included as a warning only** (`<html` + `</html>`): Warning once per pipeline, Debug always, carrying the first 200 characters. Root-element guessing excluded; the opt-in predicate declined as a genuine non-need. |
| 6 | **Rejection = pass through unchanged, always.** The leaked `Content-Range`/`Content-Length: 1` disappear because there is no longer a 500 to leak onto, not because we strip them — and stripping them is explicitly forbidden. |
| 7 | **Five new log lines, one template per cause, all Warning**, with per-line fields justified; template head only on the two content-level lines; User-Agent rejected. Recommend changing the log category to the full type name so the lines become filterable. |
| 8 | **21 committed tests** in a new `RangeAndTemplateValidityTests`, five inherited from the scratch repro (inverted), including the load-bearing `bytes=0-99` case and the 416 / multi-range / malformed-range controls. Three harness additions, one of them a log-capturing `ILoggerFactory` the suite has never had. `Still_prerenders_a_partially_captured_template` keeps passing but its comment must be corrected. |
| 9 | **Closes A2, A4 (partly), B1, B2, B5-with-a-declared-length. Does not close B3** — an error page captured as the template passes every check by construction, and is documented as a known limitation with a README warning and the opt-in predicate named as the eventual answer. |
