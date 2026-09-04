# Plan: `originalHtml` is a one-byte slice on a `Range` request

Implementation plan for [PRD-Prerendering-Range-Requests.md](./PRD-Prerendering-Range-Requests.md)
([issue #80](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/80)).

Branch: `bugfix/prerendering-range-requests`, from the merged `master`. PR #79 landed as `29db888`
while this investigation was running, so the HEAD `Content-Length` regression it introduced is
**already shipped** and is fixed here alongside #80.

## Milestones

### M1 — Investigation ✅ Complete

Three agents, in parallel, no production code written.

| Agent | Scope | Outcome |
|---|---|---|
| **A** | Mechanism, cited to ASP.NET Core source | Confirmed end to end. Found that the trigger is any satisfiable single range (including non-`bytes` units), that our `If-Range` strip *aggravates* it, and that it is deterministic rather than a race. |
| **B** | Reproduction, unit + end to end | Reproduced deterministically. Produced the `Range` matrix in the PRD, confirmed Production-only, and confirmed via Playwright that Chromium sends no `Range` on a document navigation. |
| **C** | Breadth: what else can be an unusable template | Complete — 5 Tier-A findings, 5 Tier-B, and the decisive one: **NG05104 means exactly one thing**, that the parsed template contained no element matching the app's root selector. Domino never rejects input, so there is no syntax failure mode and plausibility checks cannot work. Also found that `PassThroughAsync` already computes the completeness signal and discards it, and **corrected a claim in the previous PRD** (a downstream `Response.Clear()` *can* shrink our capture buffer). |

Also found, unprompted: **PR #79 rewrites a HEAD response's `Content-Length` to 0.** Verified with a
throwaway test (expected 547, actual 0), then removed. See the PRD.

### M2 — PRD + plan ✅

This document and the PRD.

### M3 — Reproduction tests (G1)

`Tests/Prerendering/RangeReproTests.cs` currently holds 7 scratch cases from M1. Turn them into a
committed suite that **fails on the current branch**:

- `bytes=0-0` → the reported one-byte `<`.
- `bytes=0-99` → **the load-bearing case.** The slice *starts with* `<!doctype html><html lang="en">`,
  so it defeats any "does this look like HTML?" guard. Any candidate check must be evaluated
  against this one.
- `bytes=100-200` → mid-document, no `<html` anywhere in it.
- `416` → control, already rejected today by `IsSuccessStatusCode`; pinned so a status-check change
  does not accidentally start accepting it.
- Multi-range and malformed → controls, already benign (ASP.NET Core ignores the header and serves
  a full 200); pinned for the same reason.
- HEAD → full `Content-Length` preserved, no prerender, no warning.

Assert on the template that reaches `OnSupplyData`, using the existing node-free
`PrerenderingHarness`.

### M4 — Solution team

A team decides the fix rather than defaulting to "strip the header". The PRD lists five candidates;
the questions that actually need answering:

1. Strip `Range`, narrow the status check, or both? (Defence in depth matters here because the dev
   proxy can forward a 206 from a third-party dev server that our header surgery never touches.)
2. If the status check narrows, to `200` only, or to "no `Content-Range` on the response"? Which
   fails more safely when a consumer's middleware is in the pipeline?
3. Is a completeness check on the template worth having at all, given the `bytes=0-99` result and
   given that a false positive silently disables prerendering — the exact reason the
   `ContentLength` heuristic was rejected in the previous PRD? A well-argued "status and headers are
   the right layer, content sniffing is not" is an acceptable answer.
4. GET-only gate: yes or no, and what breaks?
5. The #79 HEAD regression: fix it in `PassThroughAsync` (skip reconciliation for a bodyless
   response) or at the source (a method gate that stops HEAD reaching the capture at all)? The
   second is cleaner if (4) says yes.
6. What the rejected-template log line should contain so the *next* report of this class arrives
   with the evidence already in it.

### M5 — Implement + verify

Apply M4's decisions, then one test sweep at the end. Re-run the PRD's `Range` matrix end to end
against `Demo.Web` in Production, and re-run the previous PRD's 400-abort burst to confirm no
regression there.

### M6 — Land it

One PR against `master`, referencing #80, in the same symptom → cause → fix → test format as #79.

Version: `master` is at **10.7.0** for all six packages, which #79 shipped. This PR fixes a defect
in that release, so it needs a bump — **10.7.1** across all six if the lockstep policy from #79
holds, since the fixes are behavioural corrections rather than new API. Confirm with the maintainer:
lockstep means all six move even though only Prerendering changes, which is the standing cost that
policy accepted.

## Notes carried over from the previous PRD

- The middleware is testable without node: `PrerenderingHarness.Run(...)` builds the real delegate;
  a `RecordingPrerenderingService` captures `originalHtml` and can force a 302 to bail out before
  the prerenderer. `HttpContext.RequestAborted` and `Request.Method` are both settable on
  `DefaultHttpContext`.
- Test-design trap that still applies: assertions about the captured template must use a body shape
  that actually exercises the defect. The demo's own 547-byte `index.html` is unrepresentative in
  more than one way.
- `Response.OnStarting` is a no-op on `DefaultHttpContext`, so anything routed through it is
  unobservable in the harness and needs `TestServer`.
