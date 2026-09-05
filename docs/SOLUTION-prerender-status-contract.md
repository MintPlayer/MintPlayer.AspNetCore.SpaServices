# Solution: the prerender status-code contract and the gate split

Decision record for the second half of
[PLAN-Prerendering-Response-Headers.md](./PLAN-Prerendering-Response-Headers.md)
([PRD](./PRD-Prerendering-Response-Headers.md), [issue #81](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81)).

The header work has its own record in
[SOLUTION-prerender-header-invalidation.md](./SOLUTION-prerender-header-invalidation.md). This one
covers the status code, the prerender gate, and the API removals that followed.

## 1. The problem was a cascade, not a bug

`Response.Clear()` reset `StatusCode` to 200. Everything below follows from that one line:

1. A status assigned in `OnSupplyData` was discarded, so consumers assigned it from inside a
   `Response.OnStarting` callback, which runs after the clear.
2. A deferred status is **invisible** to the gate that decides whether to prerender, because the
   callback has not run yet. So `SkipPrerendering()` was invented to say so out of band.
3. `SkipPrerendering()` means *do not render*, so the consumer got a status but no rendered page.
4. This library's own `SpaRouteService.Redirect` ended up stacking all three.

**A rendered 404 page returned with a 404 was not expressible.** That is the actual defect; the
header loss and the status loss are the same root cause wearing two hats.

## 2. The contract

| Response state at the gate | Behaviour |
|---|---|
| 3xx **with** a `Location` header | Pass through, no render |
| 204 / 205 / 304 | Pass through, no render, **and no body** |
| `SkipPrerendering()` called | Pass through, no render |
| Any other 3xx (300 Multiple Choices) | Rendered |
| 4xx / 5xx | **Rendered, status preserved** |
| 2xx | Rendered, status preserved |
| `renderResult.StatusCode` set by node | Wins over all of the above |

Implemented as:

```csharp
if (IsRedirect(context) || !CanHaveResponseBody(context) || context.IsPrerenderingSkipped())
{
    await PassThroughAsync(context, outputBuffer);
    return;
}
```

### Why `Location` and not just 3xx

A **304** carries no `Location` and is not a redirect — it is a validator response. Treating every
3xx as a redirect would have worked by accident for 304, but for the wrong reason, and would have
closed the door on a rendered body for a 3xx that can legitimately carry one. Requiring `Location`
puts 304 where it belongs, under "cannot carry a body", alongside 204 and 205.

Considered and rejected: `3xx with Location` **plus a special case for 304**, and `any 3xx`. The
first is what shipped — the 304 case is handled by `CanHaveResponseBody` rather than by an explicit
branch, which is the same outcome with one less condition to maintain.

### Why body-less statuses are their own rule

`CanHaveResponseBody` already existed (added by PR #82 for the HEAD regression) and already knew
about 204, 205, 304 and HEAD. It guarded only the `Content-Length` reconciliation in
`PassThroughAsync`. Reusing it in the gate cost one term and introduced no new concept.

## 3. A pre-existing defect this exposed

`PassThroughAsync` copied the captured buffer unconditionally:

```csharp
await outputBuffer.CopyToAsync(context.Response.Body);
```

So a **304 emitted the entire captured `index.html` as its body** — a protocol violation on a status
defined to carry none. HEAD escaped only by accident: `StaticFileMiddleware` writes nothing for a
HEAD, so the buffer was empty and the copy was a no-op.

The fix is to gate the copy on the same `CanHaveResponseBody` that already gated the length. Two
lines, no new helper. **Independent of issue #81** — any path reaching `PassThroughAsync` with a
body-less status had this bug.

## 4. `SkipPrerendering()`: kept, and re-justified

Its documented reason dies with this fix — a status assigned in `OnSupplyData` is now visible to the
gate immediately. The default recommendation was to delete it.

**It survives because a second use has nothing to do with this bug and cannot be expressed any other
way: "render nothing, serve the shell, still return 200."** Status-based gating cannot say that,
because the status is 200 in every such case:

- render only for crawlers, serve the fast shell to everyone else;
- a per-route decision that a page is not worth rendering;
- a kill switch for when the render backend is unhealthy.

It also remains the only way to declare a status assigned inside an `OnStarting` callback, which the
middleware still cannot observe in time.

Consequences accepted: its XML docs were rewritten from scratch, because they justified the API
entirely by the `OnStarting` visibility problem this work removes; and after removing its two uses in
`SpaRouteService.Redirect` it has **zero callers in this repository**, so the documentation has to
carry the whole justification.

## 5. `OnPrepareResponse`: deleted

Ran at flush time for every request, including non-GET and excluded paths. Its only real use was
setting a header that `Clear()` would otherwise have destroyed — which is exactly what the Demo used
it for, thereby teaching the workaround as the idiom.

It could never have solved the status half of the problem, and this is the decisive point: it runs at
flush time, where **there is no context about what was loaded**. Whether the requested entity exists,
whether the user may see it, whether the slug was canonical — all of that is known in `OnSupplyData`
and nowhere else.

What is lost: the run-for-every-request behaviour. Consumers needing it write their own middleware,
which is where response headers arguably belong. After the removal the prerendering middleware
registers **no `Response.OnStarting` callback of its own at all**.

## 6. Where a status actually comes from

Two independent sources, and node reports nothing by default:

| Source | Knows | Mechanism |
|---|---|---|
| `OnSupplyData` (C#) | the entity does not exist / is forbidden — it did the lookup | assign `Response.StatusCode` directly |
| boot module (node) | the *route* does not exist; Angular fell through to its wildcard | return `{ html, statusCode }` |

`renderApplication` has no concept of an HTTP status, and the Demo's boot module returned `{ html }`
only, so `renderResult.StatusCode` was `null` on every render. The precedence rule is narrower than
"node wins": the check is `HasValue`, so node overrides only when the boot module **explicitly**
returns a status.

The effective rule changes from *"node's status, else 200"* — because `Clear()` erased everything
else — to *"node's status, else the server-assigned status, else 200"*. The middle term is the fix.

**This makes `ISpaPrerenderingService` the owner of the response status**, and therefore makes the C#
route table authoritative: a URL matching no route in `BuildRoutes` renders the SPA with a 200, which
is invisible in a browser and wrong for crawlers. That obligation is now stated in the README.

## 7. `SpaRouteService.Redirect`

Eight lines to one:

```csharp
context.Response.Redirect(url, permanent: true);
```

`permanent: true` is **kept**. Its old comment justified it mechanically — *"Response.Redirect
defaults to 302 and would otherwise overwrite a status code assigned before this callback runs"* —
which described the ordering workaround rather than the intent. The intent is real: a route redirect
is a canonicalisation, so 301 is correct and the comment now says that instead. Changing it to 302
was considered and rejected as a silent behaviour change to every existing canonical redirect.

## 8. Verification

- `Renders_the_page_and_keeps_a_404_assigned_in_on_supply_data` — the headline case: node is invoked
  **and** the response is 404 with the rendered body.
- `Skips_prerendering_for_a_redirect_assigned_in_on_supply_data`,
  `Prerenders_a_3xx_that_carries_no_location`,
  `Writes_no_body_for_a_status_that_cannot_carry_one` (204/205/304),
  `Pass_through_writes_no_body_when_the_status_forbids_one`,
  `The_render_results_status_wins_over_one_assigned_in_on_supply_data`,
  `Still_honours_a_status_assigned_from_an_on_starting_callback` (the legacy pattern is not broken),
  `Fails_with_a_named_error_when_the_response_has_already_started`.
- `RedirectTests.Writes_the_redirect_immediately_rather_than_deferring_it` — inverted from
  `Does_not_touch_the_response_until_it_starts`, with a comment recording why.
- End to end against `Demo.Web` over HTTPS: `/` still returns `301 Moved Permanently` with its
  `Location`, from a `Redirect` that no longer defers or skips.

A later removal experiment deleted each gate term individually and re-ran the suite: `IsRedirect`,
`CanHaveResponseBody` (both sites) and `IsPrerenderingSkipped` each break tests. See the PRD's
redundancy review.
