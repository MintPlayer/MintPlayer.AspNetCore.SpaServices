# Solution decision — Defect 1 (`GetBuffer()` decoded without a length) and Defect 1b (UTF-8 BOM)

M4 deliverable for [PRD-Prerendering-Aborted-Requests.md](./PRD-Prerendering-Aborted-Requests.md)
(Defect 1, Defect 1b) and [PLAN-Prerendering-Aborted-Requests.md](./PLAN-Prerendering-Aborted-Requests.md)
M4 question 1. **Decision document only — no production code here. M5 implements.**

Scope: the single expression at
`MintPlayer.AspNetCore.SpaServices.Prerendering/Prerendering/SpaPrerenderingExtensions.cs:149`

```csharp
{ "originalHtml", Encoding.UTF8.GetString(outputBuffer.GetBuffer()) }
```

Nothing else. The abort early-return (Defect 2), the G5 empty-template guard and Workstream 3's
cancellation threading are other agents' decisions and are not touched or pre-empted here.

---

## Recommendation in one block

```csharp
// The captured response is decoded as UTF-8; see "Charset" below for why that is a documented
// assumption rather than a bug. TryGetBuffer (not GetBuffer) so the offset and the count both come
// from the stream: GetBuffer() returns the whole internal array, whose length is Capacity, and a
// growable MemoryStream doubles (256, 512, 1024, ...), so decoding it appends up to Capacity-Length
// trailing NULs to the template.
var buffer = outputBuffer.TryGetBuffer(out var segment)
    ? segment
    : new ArraySegment<byte>(outputBuffer.ToArray());

// Encoding.UTF8.GetString does not strip a byte-order mark. index.html saved as UTF-8-with-BOM
// (Visual Studio's Windows default) would otherwise put U+FEFF in front of <!doctype> in the
// template handed to renderApplication({ document }).
var offset = buffer.Offset;
var count = buffer.Count;
if (count >= 3 && buffer.Array![offset] == 0xEF && buffer.Array[offset + 1] == 0xBB && buffer.Array[offset + 2] == 0xBF)
{
    offset += 3;
    count -= 3;
}

var customData = new Dictionary<string, object>
{
    { "originalHtml", Encoding.UTF8.GetString(buffer.Array!, offset, count) }
};
```

One string allocation, no byte-array copy, no `Capacity` anywhere, origin-safe by construction,
BOM-free. Everything below is the reasoning.

---

## 1. Decode strategy — `TryGetBuffer`, not `GetBuffer(…, 0, Length)` and not `ToArray()`

### The three options the PRD names, and why each is second-best

| Option | Correct? | Cost | Verdict |
|---|---|---|---|
| `GetString(GetBuffer(), 0, (int)Length)` | Only because `_origin == 0` | 1 string | **Rejected** — correct by coincidence |
| `GetString(ToArray())` | Always | 1 string **+ a full byte-array copy** | Rejected — pays for safety it needn't |
| `GetString(GetBuffer().AsSpan(0, (int)Length))` | Same coincidence as row 1 | 1 string | Rejected — a `Span` changes nothing about the bug; `GetBuffer()` is still the wrong accessor |
| **`TryGetBuffer` → `GetString(Array, Offset, Count)`** | Always | 1 string | **Chosen** |

The PRD frames this as a trade-off between an extra copy (`ToArray()`) and the origin-safety
landmine (`GetBuffer(), 0, Length`). **That framing has a third exit, and it is strictly better than
both ends of the trade-off, so I am not taking either side of the stated trade-off — I am declining
it.** `MemoryStream.TryGetBuffer` returns `new ArraySegment<byte>(_buffer, _origin, _length - _origin)`:
the offset and the count are supplied *by the stream*, from the same two private fields the
`_origin == 0` argument is about. There is no arithmetic for a future maintainer to get wrong and no
invariant for them to rely on unknowingly.

That is the deciding point. The PRD's question is "is the offset-stream hazard theoretical enough to
note in a comment?" — my answer is that the hazard's *cost* is what matters, not its likelihood, and
here the cost of removing it is **zero bytes and zero allocations**. A comment is what you write when
the safe form costs something. `Length` (as opposed to `Length - _origin`) is also subtly wrong for
an offset stream in a second way: `MemoryStream.Length` is already `_length - _origin`, so
`GetString(buf, 0, (int)Length)` on an offset stream reads `Count` bytes starting at the *wrong
place* — it both truncates the tail and includes pre-origin bytes. Two bugs in one expression, both
latent, both invisible in review. `TryGetBuffer` makes them unrepresentable.

Against `ToArray()`: the copy is not free at the size that matters. A production `ng build`
`index.html` with inlined critical CSS is 5–60 KB (PRD, Defect 1) and this runs on **every**
prerendered request — a per-request LOH-adjacent allocation for a buffer we are about to decode and
throw away. `ToArray()` also *reintroduces* nothing: it is origin-safe, so this is purely a cost
comparison, and `TryGetBuffer` wins it while being equally safe.

### Why the `else`/`ToArray()` fallback stays anyway

`TryGetBuffer` returns `false` for a stream constructed with `publiclyVisible: false`. That is not
reachable today — line 108 is a literal `new MemoryStream()`, always publicly visible — but the
fallback is one expression, it is the branch a future "let the caller supply the capture stream"
change would land on, and without it `segment` is a silently-default `ArraySegment` (null `Array`,
zero `Count`) that would hand node an **empty template** — i.e. it would fail as NG05104, the exact
symptom this whole PR exists to remove. Never let this code path degrade to empty silently. The
fallback is not defensive padding; it is the difference between a correct decode and reproducing the
reported bug.

### Decoder leniency is load-bearing — do not "improve" it

Keep `Encoding.UTF8` (the shared, replacement-fallback instance). Do **not** substitute
`new UTF8Encoding(false, throwOnInvalidBytes: true)`. Defect 2 case (b) is a *partial* copy, which
can cut mid-multi-byte-sequence; a throwing decoder would convert an already-degraded template into
an unhandled `DecoderFallbackException` out of the middleware on aborted requests — a new failure
mode, on the abort path, which is not this workstream's to change. Replacement characters in a
template that is about to be rejected by another guard are the right outcome.

---

## 2. Charset — hard-code UTF-8, and say so

**Decision: keep UTF-8 hard-coded. Document the assumption in a comment at the decode site.** No
media-type parsing, no `Encoding.GetEncoding(charset)`, no fallback chain.

Three reasons, in order of weight:

1. **A charset-aware decode would fix one end of a two-ended pipeline and make the result *more*
   confusing, not less.** The write side is already unconditionally UTF-8: `ServePrerenderResult`
   uses `context.Response.WriteAsync(renderResult.Html)` (`:283`), whose default encoding is UTF-8
   regardless of the charset the response declares, and the node RPC in between is JSON — also
   UTF-8. So for a hypothetical ISO-8859-1 template, a charset-aware decode would produce a
   correctly-decoded .NET string that is then re-encoded as UTF-8 bytes and shipped under a
   `charset=iso-8859-1` header. Today that consumer gets mojibake in, mojibake out — self-evidently
   broken and traceable. With a half-fix they get *correct-looking* .NET strings and wrong bytes on
   the wire, which is harder to diagnose. Honouring the charset is only defensible as part of
   making the whole path charset-aware (decode, RPC, and the response write), and that is a
   different, larger change with no requester.
2. **Nothing realistic emits anything else.** `ng build` writes UTF-8; the static-files middleware
   maps `.html` to `text/html` with **no charset parameter at all**, so in production there is
   usually nothing to honour and the "honour it" branch would fall back to UTF-8 anyway. In
   development the proxy copies the Angular dev server's `text/html; charset=utf-8`. And the
   template is not arbitrary user content — it is the SPA's own build output, whose toolchain is
   UTF-8 by definition.
3. **It buys complexity in the one place the PRD is trying to simplify.** A charset-aware decode
   needs media-type parameter parsing (this file already had to fix its media-type comparison once —
   see the `IsHtmlContentType` comment about `"TEXT/HTML"` and `"text/html ; charset=utf-8"`), an
   unknown-charset policy, and an `Encoding.GetEncoding` call that throws on a bogus name — i.e. a
   new way for a malformed `Content-Type` to 500 a request that previously rendered.

What would break for a consumer serving a non-UTF-8 template: nothing *newly*. They are already
broken today in exactly the same way (their bytes are decoded as UTF-8), and this change does not
move them. If one ever appears, the fix is the whole-path change, and the comment left at the decode
site is where they will start reading.

---

## 3. BOM (Defect 1b) — byte-level skip, same commit, its own test

### How

**Byte-level BOM skip folded into the offset/count computation** (see the recommendation block),
not the two alternatives:

- `TrimStart('﻿')` **(rejected)** — allocates a second string on top of the first for the exact
  case it fixes; trims a *run* of U+FEFF rather than the single BOM that byte prefix `EF BB BF`
  actually represents (subsequent U+FEFF characters are legitimate zero-width no-break spaces in the
  document body and are not ours to delete); and it works on the decoded form, so it is one more
  transformation layered after the decode rather than part of it.
- `new StreamReader(stream, detectEncodingFromByteOrderMarks: true)` **(rejected)** — it is not a
  BOM stripper, it is an *encoding detector*, so it silently reopens decision 2 that I just closed:
  a template that happens to begin with `FF FE` would be decoded as UTF-16LE and fed into a UTF-8
  write path. It also brings a `Stream`-position dependency, a `ReadToEndAsync` (a second async
  boundary and a second large allocation) and a disposable to own, for a three-byte check. Wrong
  tool.

The byte-level form costs two comparisons and one `if`, allocates nothing extra, keeps the whole
decode as one `GetString` call, and — because it adjusts `offset` **and** `count` — is the only form
where getting it wrong is caught by an off-by-one in a test (see test 5) rather than being invisible.

### One fix or two?

**One commit, two named tests.** They are the same expression: after choosing `TryGetBuffer`, the
length fix and the BOM fix are both "compute the right `offset` and `count`, then decode once".
Splitting them into two commits means the second rewrites the first's only line, so the diff would
show churn rather than history, and the intermediate state has no independent meaning. The PRD's
point that the length fix "does NOT address" the BOM is a statement about *coverage*, and coverage
is what tests assert — so the separation lives in the test suite, where it is useful, not in the
commit graph, where it is not. Each gets its own `[Fact]` with its own name, and test 5 asserts they
compose. (This does not touch the one-PR policy either way — both were always landing together.)

---

## 4. Tests — exactly what must be committed

In `MintPlayer.AspNetCore.SpaServices.Tests/Prerendering/`, using the existing
`PrerenderingHarness` (`SpikeHarnessTests.cs`) seam: `RecordingPrerenderingService` captures
`customData["originalHtml"]` and bails out with a 302 before node.

The harness's current `HtmlPage(string)` inner pipeline does a **single** `WriteAsync`. Cases 1 and 5
need a multi-write pipeline, so M5 must add a chunk-aware helper (e.g.
`HtmlPageInChunks(string html, params int[] chunkSizes)`) rather than reusing `HtmlPage`. That is a
test-infrastructure addition, not a change to the assertions of the existing tests.

| # | Test | Body | Must assert | On `master` |
|---|---|---|---|---|
| 1 | **>16 KB, multi-write — the load-bearing one** | 20,000 ASCII bytes of realistic-looking HTML, written as **16384 + 3616** (matching `SendFileFallback`'s `1024*16` buffer) | `captured.Length == 20000`; `captured == body`; `Assert.DoesNotContain('\0', captured)` | **RED** — capacity 32768 ⇒ 12,768 trailing NULs |
| 2 | **<256 bytes, single write** (the rewritten existing test) | the existing 76-byte `IndexHtml` | `captured == IndexHtml`; `captured.Length == 76` | **RED** — capacity 256 ⇒ 180 NULs |
| 3 | **The trap, pinned as a control** | a realistic 547-byte template, one write | `captured == body` — plus a comment stating **this one is green on `master`** and why (capacity 547 == length, zero padding), so nobody later mistakes it for the regression test | green |
| 4 | **BOM stripped** | `EF BB BF` + `IndexHtml`, one write | `captured[0] != '﻿'`; `captured == IndexHtml` | **RED** |
| 5 | **BOM + >16 KB compose** | `EF BB BF` + the 20,000-byte body, written as 16384 + 3619 | `captured == body` (no leading U+FEFF **and** no truncated last character) | **RED** |
| 6 | **BOM only at the head** | `IndexHtml` with a U+FEFF (﻿) in the middle of `<body>` | the interior U+FEFF **survives** | green (locks in that we strip a BOM, not all U+FEFF — this is the test that fails if someone "simplifies" the fix to `TrimStart`) |

Test 1 is the one that satisfies G1 honestly. The PRD's trap is that a realistic small template pads
zero, so an "equals the body exactly" assertion is green on `master` with the bug fully present; test
1 is >16 KB **and** multi-write, so it exercises the doubling growth path (`EnsureCapacity`:
`max(requested, 256)` then double) that production actually hits. Test 5's odd chunk split (3619, not
3616) keeps the total at 20,003 so the BOM skip and the growth path are exercised simultaneously and
an `offset += 3` without a matching `count -= 3` shows up as a lost final byte.

### The existing verbatim-padding assertion: **rewrite, do not delete**

`SpikeHarnessTests.Shows_the_GetBuffer_padding_defect_verbatim` currently asserts the bug:

```csharp
Assert.Equal(256, captured.Length);
Assert.Equal(new string('\0', 180), captured[IndexHtml.Length..]);
```

It becomes test 2 above: same body, same harness, assertions inverted to the fixed behaviour
(`captured == IndexHtml`), renamed off `Shows_the_…_defect_verbatim` (e.g.
`Does_not_pad_a_small_template_with_NULs`), and the `76 bytes ⇒ capacity 256 ⇒ 180 NULs` arithmetic
**kept in the comment** as the record of what the defect was. Reasons not to delete it: it is the
only test covering the sub-256 growth branch, which is a genuinely different path from test 1's
doubling; and the *first* assertion it makes — `Assert.Equal(76, Encoding.UTF8.GetByteCount(IndexHtml))` —
pins the fixture size that all the arithmetic in the comments depends on.

Also note for M5: `Captures_the_original_html_without_launching_node` and
`Works_even_without_a_registered_INodeServices` both use `Assert.StartsWith(IndexHtml, …)`, which
was written to tolerate the padding. They stay green after the fix, but `StartsWith` should be
tightened to `Assert.Equal` in the first (it is a capture-fidelity test) and left as-is in the second
(its subject is the node process count, not the string).

---

## 5. Release-note wording

> **Behaviour change:** the HTML template passed to `ISpaPrerenderingService.OnSupplyData` as
> `customData["originalHtml"]` (and on to the prerenderer) is now exactly the bytes the inner
> pipeline wrote, decoded as UTF-8. Previously it could carry up to several thousand trailing NUL
> (`\0`) characters — the unwritten remainder of the capture buffer's capacity, affecting responses
> over 16 KB and under 256 bytes — and a leading byte-order mark when `index.html` was saved as
> UTF-8-with-BOM. Consumers that inspect, hash, diff or snapshot `originalHtml`, or that match on its
> length or its final characters, will see different (correct) input.

---

## Non-goals of this decision

- The abort early-return, the empty/whitespace-template guard, and the `IsHtmlContentType(string?)`
  nullability fix — other owners.
- Making the prerendering path charset-aware end to end (decode + RPC + response write). Declined
  above with reasons; if it is ever wanted it is its own change.
- Replacing the `MemoryStream` capture with `IHttpResponseBodyFeature`/`PipeWriter` — an explicit
  PRD non-goal. Worth noting that `TryGetBuffer` is the accessor that survives such a rewrite
  unchanged, whereas `GetBuffer(), 0, Length` is the one that would have to be re-audited.
