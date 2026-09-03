# Solution: Workstream 3 — request-scoped cancellation token threading

Decision document for **Workstream 3** of
[PRD-Prerendering-Aborted-Requests.md](./PRD-Prerendering-Aborted-Requests.md) (goals **G7** and
**G8**, plan milestones **M2b** → **M4** → **M5**).

**This workstream is independent of both reported defects.** Nothing below is a fix for the corrupt
`originalHtml` (Defect 1/1b) or for prerendering-after-abort (Defect 2); those are owned by the two
other solution agents and are not touched here. W3's justification stands on its own: the request's
cancellation token should reach the one call in the prerendering path that can block for up to 60
seconds, and today it does not.

**No production code was written for this document.** M5 implements it.

## 0. Verification of the inherited claims

Everything W3 relies on was re-checked against the tree at `c003c4f`, not inherited on trust.

| Claim | Verified? | Evidence |
|---|---|---|
| Exactly one genuinely wrong callsite | ✅ | `Prerenderer.cs:76` calls `nodeServices.InvokeExportAsync<RenderToStringResult>(GetNodeScriptFilename(...), "renderToString", …)`. The first argument is a `string`, so overload resolution binds `InvokeExportAsync<T>(string, string, params object[])` — `NodeServicesImpl.cs:39-42`, which hard-codes `CancellationToken.None` at line 41. |
| `Prerenderer` is not public API | ✅ | `Prerenderer.cs:12` is `internal static class Prerenderer`. The `public` on `RenderToString` (line 65) is inert — an accessibility ceiling, not an export. A repo-wide grep for the identifier `Prerenderer` outside its own file and its one caller returns nothing. |
| Only two potential callers exist | ✅ | `SpaPrerenderingExtensions.cs:168` is the **only** callsite in the repo, and it calls the **nine-argument** overload (line 65) directly. The `HttpContext`-taking overload at line 18 is **currently dead code** — no production or test caller. No test calls `Prerenderer.RenderToString` at all (`grep` over `Tests/Prerendering/*.cs`). |
| `InternalsVisibleTo` grants the test assembly access | ✅ | `MintPlayer.AspNetCore.SpaServices.Prerendering.csproj:41` — `<InternalsVisibleTo Include="MintPlayer.AspNetCore.SpaServices.Tests" />`. NodeServices grants the same at its `.csproj:41`. |
| **Therefore: changing `RenderToString`'s signature is not a breaking change.** | ✅ **Confirmed, not inherited.** | The type is `internal`, the only caller is in the same assembly, and the test assembly (the only other thing that can see it) does not call it. There is no consumer to break, even in principle. |
| The `applicationStoppingToken` trap is real | ✅ | `Prerenderer.cs:16` `private static StringAsTempFile NodeScript;` (static, built once under `CreateNodeScriptLock`, and `GetNodeScriptFilename` only recreates it `if (NodeScript == null)`), and `StringAsTempFile`'s constructor does `applicationStoppingToken.Register(EnsureTempFileDeleted)` (`Util/StringAsTempFile.cs:29`). Linking `RequestAborted` into that token therefore deletes the shared `prerenderer.js` on the first request that completes, permanently, for the whole process. **The trap is confirmed and drives decision 1 below.** |
| `SpaProxy` is the linking precedent | ✅ | `SpaProxy.cs:59-61`, `CreateLinkedTokenSource(context.RequestAborted, applicationStoppingToken).Token`. One correction to "copy this": it **never disposes the source** — see decision 2. |
| `OutOfProcessNodeInstance` overwrites the caller's token | ✅ | `OutOfProcessNodeInstance.cs:118-120`, with the classification at `:138-140` reading `timeoutSource.IsCancellationRequested` only. |

## 1. The signature change to `Prerenderer.RenderToString`

### Decision

**Add one required `CancellationToken requestCancellationToken` parameter, in last position, to both
overloads. No default value. Leave `applicationStoppingToken` exactly as it is, in name, position
and meaning.**

```csharp
internal static Task<RenderToStringResult> RenderToString(
    string applicationBasePath,
    INodeServices nodeServices,
    CancellationToken applicationStoppingToken,
    JavaScriptModuleExport bootModule,
    HttpContext httpContext,
    object customDataParameter,
    int timeoutMilliseconds,
    CancellationToken requestCancellationToken)          // ← new, last

public static Task<RenderToStringResult> RenderToString(
    string applicationBasePath,
    INodeServices nodeServices,
    CancellationToken applicationStoppingToken,
    JavaScriptModuleExport bootModule,
    string requestAbsoluteUrl,
    string requestPathAndQuery,
    object customDataParameter,
    int timeoutMilliseconds,
    string requestPathBase,
    CancellationToken requestCancellationToken)          // ← new, last
```

and the body of the nine-argument overload becomes a call to the **token-taking** RPC overload:

```csharp
return nodeServices.InvokeExportAsync<RenderToStringResult>(
    requestCancellationToken,                            // ← the whole point of W3
    GetNodeScriptFilename(applicationStoppingToken),     // ← unchanged, deliberately
    "renderToString",
    applicationBasePath, bootModule, requestAbsoluteUrl, requestPathAndQuery,
    customDataParameter, timeoutMilliseconds, requestPathBase);
```

### Why *add* rather than *replace*

Replacing `applicationStoppingToken` with a pre-linked token is the shape that looks cleanest and is
the one bug this workstream must not ship. The parameter has two consumers with **different
lifetimes**:

- the RPC — wants *request* lifetime;
- `GetNodeScriptFilename` → `new StringAsTempFile(script, token)` — wants *process* lifetime,
  because `NodeScript` is `static` and is never rebuilt once non-null.

One parameter cannot carry both. Two parameters is the minimum honest shape. The XML doc on each
must say which lifetime it is, so the next reader does not re-derive the trap the hard way.

### Why *required*, with no default

`= default` (or `= default(CancellationToken)`) would compile every existing and future callsite
silently and reintroduce `CancellationToken.None` at the RPC — which is *precisely* how the current
bug happened: `params object[] args` made the token-less overload bind without a diagnostic. A
required parameter converts that silent failure into a compile error. The cost of "required" is
normally that it breaks callers; here there are **zero** external callers (§0), and exactly one
internal one. So the cost is nil and the guarantee is total. This is the cheapest available instance
of *defining the error out of existence*.

### Why *last* position, and not next to `applicationStoppingToken`

Grouping the two tokens adjacently reads better and would document the lifetime distinction in one
glance — but it creates two adjacent same-typed parameters whose accidental transposition is exactly
the catastrophic case: swap them and every completed request deletes the shared `prerenderer.js`,
with no compiler complaint and no immediate symptom (the first request succeeds; the *second* fails
on a missing script). Last position puts five parameters between them. Combined with the
recommendation below, a transposition becomes hard to write and obvious to read.

**M5 must pass it as a named argument at the callsite**, matching the existing style there (the
current call already names `customDataParameter:`, `timeoutMilliseconds:`, `requestPathBase:`):

```csharp
requestCancellationToken: prerenderCts.Token);
```

### Why this name

`requestCancellationToken` names the *lifetime*, which is the property that distinguishes it from
`applicationStoppingToken`'s process lifetime — the exact distinction the trap turns on. Rejected
alternatives: `cancellationToken` (says nothing, and there are two tokens here); `requestAborted`
(a lie — the value is a *linked* token, not `HttpContext.RequestAborted`, and someone would
"simplify" it back to the raw property, dropping shutdown responsiveness); `invocationCancellationToken`
(accurate about its use but silent about its lifetime, which is the load-bearing bit).

### The dead `HttpContext` overload (line 18)

It has no callers. Two defensible options:

1. **Give it the same new parameter** (recommended). Keeps the two overloads consistent, costs one
   line, and keeps the door open for a future caller that has an `HttpContext`.
2. Delete it as dead code.

Recommend (1), and explicitly **do not** make it link the token itself from
`httpContext.RequestAborted`. It is tempting — it has the `HttpContext` right there — but then the
two overloads would differ in *who* owns the linking, and a caller reading the nine-argument one
would have no way to know the seven-argument one silently does more. Same parameter, same contract,
linking in exactly one place (§2). Deleting it (option 2) is also acceptable and is a separate
judgement call about dead code, not a W3 decision.

## 2. Where the linking happens

### Decision

**In the middleware (`SpaPrerenderingExtensions.cs`), as a `using var` declared immediately before
the render call. `Prerenderer` never creates a CTS and never reads `HttpContext.RequestAborted`.**

```csharp
// Stop the prerender if either the client disconnects or the server shuts down. Note this is
// deliberately NOT the same token as applicationStoppingToken below: that one owns the
// process-lifetime prerenderer.js temp file and must never see RequestAborted.
using var prerenderCts = CancellationTokenSource.CreateLinkedTokenSource(
    context.RequestAborted,
    applicationStoppingToken);

var (unencodedAbsoluteUrl, unencodedPathAndQuery) = GetUnencodedUrlAndPathQuery(context);
var renderResult = await Prerenderer.RenderToString(
    applicationBasePath,
    nodeServices,
    applicationStoppingToken,
    moduleExport,
    unencodedAbsoluteUrl,
    unencodedPathAndQuery,
    customDataParameter: customData,
    timeoutMilliseconds: options.TimeoutMilliseconds,
    requestPathBase: context.Request.PathBase.ToString(),
    requestCancellationToken: prerenderCts.Token);

await ServePrerenderResult(context, renderResult);
```

### Why the middleware, not `Prerenderer`

- The middleware is the only layer that *has* both inputs: `context` per request, and
  `applicationStoppingToken` captured once at pipeline-build time (`:56`). The nine-argument
  `Prerenderer` overload has no `HttpContext` at all, so it structurally cannot link.
- `Prerenderer` stays a pure function of its arguments: no ambient state, no hidden lifetime policy,
  directly unit-testable by passing whatever token the test wants (§6).
- It matches the in-repo precedent (`SpaProxy.cs:59-61`), so the two SPA paths handle cancellation
  the same way and a reader learns the pattern once.
- Cancellation *policy* ("which signals should stop a prerender?") is a middleware concern.
  `Prerenderer` should not get a vote.

### Placement: as late as possible

Declared **after** the line-161 status guard, immediately before the render. Every earlier exit
(excluded path, `!canPrerender`, non-2xx after `OnSupplyData`, and the abort early-return the Defect
2 agent is adding) then never pays for the CTS or its two registrations. This also means W3's change
cannot interact with the abort early-return: by the time `prerenderCts` exists, that guard has
already returned.

### Ownership and disposal — `using var` is correct here

**Yes, `using var` is correct despite the awaits.** `using var` disposes at the end of the enclosing
block, and both `await`s that need the token (`RenderToString`, and `ServePrerenderResult` after it)
are *inside* that block. The `await` suspends the method with the scope still live; disposal happens
only after the continuation resumes past them. The broken shape — creating the CTS in a `using`
block, starting the task inside, and awaiting it *outside* — does not occur and must not be
introduced.

**Do not copy `SpaProxy`'s `.Token` without a `using`.** `CreateLinkedTokenSource(...).Token`
discards the source, but the source is **not** immediately collectable: it holds registrations on
both parent tokens, and one of those parents (`ApplicationStopping`) lives for the entire process.
So each undisposed linked CTS is retained until application shutdown — a genuine per-request
retention leak, small per request and unbounded over uptime. `using var` costs one word and removes
it.

### Noted, adjacent: `SpaProxy.cs:59-61` has that leak today

Same one-word fix (`using var proxyCts = CancellationTokenSource.CreateLinkedTokenSource(...)`, then
use `proxyCts.Token`). Verified safe: every use of `proxyCancellationToken` in
`PerformProxyRequest` — `AcceptProxyWebSocketRequest` (`:77`), `httpClient.SendAsync` (`:83-86`),
`CopyProxyHttpResponse` (`:98`) — is `await`ed inside the method, and nothing captures the token
beyond it. **Recommendation: include it** (one line, same file family, same subject, and the one-PR
policy means there is no later). Flagged explicitly rather than folded in silently, because it is
not needed to make G7 work — if the reviewer prefers a tighter W3 diff, dropping it costs nothing
but leaves a known leak in the code this document cites as the precedent.

## 3. The three cosmetic callsites — **skip all three**

| Callsite | Verdict |
|---|---|
| `SpaPrerenderingExtensions.cs:140` `outputBuffer.CopyToAsync(context.Response.Body)` | **Skip** |
| `SpaPrerenderingExtensions.cs:163` `outputBuffer.CopyToAsync(context.Response.Body)` | **Skip** |
| `SpaPrerenderingExtensions.cs:283` `context.Response.WriteAsync(renderResult.Html)` | **Skip** |

This is not "skip because it is effort". It is skip because on these three calls a token is a
**net negative**.

### `:140` and `:163` — a token converts a harmless no-op into a thrown exception

Both are pass-through exits on a request that will *not* be prerendered, and both are reachable on
an already-aborted request. Spike 6 established that on an aborted request these copies are
provably zero-work: Kestrel silently discards post-abort writes, and `MemoryStream.CopyToAsync`
additionally short-circuits at `if (n == 0)`. Add the token and `MemoryStream.CopyToAsync`
front-checks it and returns `Task.FromCanceled` — so an `OperationCanceledException` escapes the
middleware on requests that previously passed through quietly, for a write whose bytes were going to
be discarded anyway.

The PRD flags exactly this hazard for the abort-path copy. The same reasoning applies here, and
`:140` is not a hypothetical: Spike 4 found that on the **development dev-proxy** abort path the
response has `ContentType == null`, so `canPrerender` is false and the request exits through **`:140`
specifically**. Threading the token there would turn today's silent, correct dev-mode behaviour into
a logged exception on every aborted dev request. Upside: none.

### `:283` — no benefit, and it breaks the existing test harness

By the time `:283` runs, the code has just `await`ed a now-cancellable RPC. If the request was
aborted, *that* await is where cancellation is observed; `:283` is not reached. In the vanishing
window where the abort lands between the two, the write is silently discarded by Kestrel (Spike 6) —
correct behaviour for a disconnected client.

There is also a concrete cost. Reaching the token inside `ServePrerenderResult` means either
threading a new parameter through it, or reading `context.RequestAborted` inline. The parameter
option breaks the test suite: `SpaPrerenderingReflection.ServePrerenderResult`
(`Tests/Prerendering/SpaPrerenderingExtensionsTests.cs:37-40`) invokes it by reflection with a
fixed two-element argument array, so an added parameter fails with `TargetParameterCountException`
and takes ~8 passing tests down with it. Paying that to gain nothing is not a trade worth making.
The inline option avoids the signature break but keeps the zero-benefit exception path.

### The principle worth stating in the PR

**Thread the token where cancellation can abandon real work; do not thread it where it can only
manufacture exceptions.** In this middleware there is exactly one call that blocks on something
slow and remote — the node RPC, up to `TimeoutMilliseconds` (60 s by default). Everything else is a
local buffer copy that an aborted request discards for free. G7 is a one-callsite change, and saying
so plainly is more useful than a sweep that pads the diff with regressions.

## 4. The timeout-vs-cancellation message race — **fix it now**

### The race

`OutOfProcessNodeInstance.cs:118-120` overwrites the caller's token with the combined
caller+timeout token, deliberately, so nothing below can use the un-timed-out one. The classification
in the `catch (TaskCanceledException)` at `:138-140` then has only `timeoutSource` left to inspect:

```csharp
cancellationToken = combinedCancellationTokenSource.Token;   // :120 — caller's token now unreachable
…
catch (TaskCanceledException)
{
    if (timeoutSource.IsCancellationRequested)                // :140
        throw new NodeInvocationException("The Node invocation timed out after …", "…ensure your Node.js function always invokes the supplied callback…");
    throw;
}
```

If a client disconnect and the 60 s timeout land in the same window, `timeoutSource` has fired, so
the disconnect is reported as a node authoring bug.

### Why this is not merely cosmetic

It changes the **exception type**, not just the message. Correctly classified, an abort surfaces as
`OperationCanceledException` — which callers, ASP.NET Core's own logging, and any
`catch (OperationCanceledException)` handler treat as "client went away, not an error". Misclassified,
it surfaces as `NodeInvocationException`, which is an error: a 500, a stack trace, and a support
question about a callback the user's code invoked correctly. That is a behavioural
misclassification with a plausible cost, sitting on the exact path W3 is about to make reachable
for the first time.

### Why now rather than "noted and left"

- **W3 is what makes it reachable.** Today no cancellation ever reaches the RPC, so this branch is
  effectively dead. After G7, every user navigating away during a slow prerender rolls these dice.
  Leaving it means shipping a new misleading log line *as a consequence of our own change* and
  calling it someone else's problem.
- **The fix is three lines** and adds no new concept.
- **The separate-package concern is real but small.** `MintPlayer.AspNetCore.NodeServices` ships on
  its own, so it needs its own release-note line and its own version bump — but this repo already
  does exactly that in lockstep (`fbd073a`, "Bump version to 10.3.0 for packages depending on
  NodeServices"), so the mechanics exist. And the one-PR policy means there is no later PR to defer
  to.

### The change

```csharp
// Keep the caller's token so a cancellation can still be told apart from a timeout below. The
// combined token is about to overwrite `cancellationToken` on purpose, which otherwise leaves
// the catch block unable to see which of the two fired.
var callerCancellationToken = cancellationToken;
cancellationToken = combinedCancellationTokenSource.Token;
…
catch (TaskCanceledException)
{
    // Test the caller's token FIRST: when a client disconnect races the invocation timeout both
    // sources have fired, and reporting a disconnect as "your Node.js function never invoked the
    // callback" sends the reader after a bug that isn't there.
    if (timeoutSource.IsCancellationRequested && !callerCancellationToken.IsCancellationRequested)
    { /* existing timeout messages, unchanged */ }
    else
    {
        throw;
    }
}
```

Both existing timeout messages (the `connectionDidSucceed` split at `:145-167`) stay verbatim. The
only change is which branch a *raced* cancellation takes.

### No unit test for this one, deliberately

`OutOfProcessNodeInstance`'s constructor calls `LaunchNodeProcess` and `ConnectToInputOutputStreams`
(`:72`, `:75`), so the class cannot be instantiated without a real node process; nothing in the test
suite touches it today (only `NodeServicesImpl`, via a fake `INodeInstance`). Deterministically
testing a race inside it would mean extracting a test seam, which is a refactor of a shipped package
well outside W3's remit. **Recommendation: make the fix with the comment above and no test, and say
so in the PR** rather than pretending the gap isn't there. If the reviewer wants coverage, the
smallest honest option is to extract the two-token classification into an `internal static` helper
and unit-test that — worth ~10 lines, and a legitimate reviewer ask, but not proposed here.

## 5. G8 — XML-doc wording for the token-less overloads

Confirmed decision: **keep both token-less overloads, do not `[Obsolete]` them, document them.**
`[Obsolete]` would emit `CS0618` in consumers calling a perfectly correct API (plenty of node
invocations have no request to scope to), exporting our internal mistake to every user;
`[Obsolete(error: true)]` or removal would be source-breaking on a shipped public interface. The
real gap is that the docs never mention cancellation at all.

Add to **both** token-less overloads in `INodeServices.cs` (`:20` `InvokeAsync`, `:40`
`InvokeExportAsync`):

```xml
/// <remarks>
/// This overload invokes Node.js with <see cref="CancellationToken.None"/>. The invocation cannot be
/// cancelled once started, and runs until it completes or the configured invocation timeout elapses.
/// That is the intended behaviour for work that is not scoped to a single HTTP request, such as
/// start-up, background, or shared one-off invocations.
/// For request-scoped work, call the overload that takes a <see cref="CancellationToken"/> and pass a
/// token linked to <c>HttpContext.RequestAborted</c>, so that a client disconnect releases the
/// calling thread instead of waiting out the timeout.
/// </remarks>
```

Add to **both** token-taking overloads (`:30`, `:51`), because the limit is worth stating where
someone will actually rely on it, and it is the honest reading of Spike 8:

```xml
/// <remarks>
/// Cancelling <paramref name="cancellationToken"/> abandons the .NET side of the RPC: the call throws
/// an <see cref="OperationCanceledException"/> and the calling thread is released. It does not signal
/// Node.js. The RPC protocol has no abort channel, so the Node.js function runs to completion and its
/// result is discarded.
/// </remarks>
```

Optionally tighten the existing one-liner on the `cancellationToken` parameter itself from
"…that can be used to cancel the invocation" to "…that can be used to stop waiting for the
invocation", which is what it actually does. Cheap, and it removes the implication of a remote abort
the protocol cannot deliver.

## 6. Tests

All in `MintPlayer.AspNetCore.SpaServices.Tests`, xunit, `net10.0`, following the existing shapes:
`NodeServicesImplTests` (fake `INodeInstance` + `CountingFactory`) and `PrerenderingHarness` /
`SpikeHarnessTests` (real middleware pipeline, no node process). No test may launch node.

### New file: `Tests/NodeServices/NodeServicesImplTests.cs` (additions to the existing class)

`FakeNodeInstance` currently drops the `cancellationToken` argument on the floor. Extend it to
record it (`public CancellationToken SeenToken { get; private set; }`), which is additive and leaves
every existing test unchanged.

**T1 — `Forwards_the_callers_cancellation_token_to_the_instance`**
Invoke `services.InvokeExportAsync<string>(cts.Token, "./module", "render")` and assert
`instance.SeenToken == cts.Token`. Pins the forwarding that G7 depends on and that nothing asserts
today.

**T2 — `Passes_no_cancellation_token_for_the_token_less_overload`**
Invoke `services.InvokeAsync<string>("module")` and assert `instance.SeenToken == CancellationToken.None`
and `!instance.SeenToken.CanBeCanceled`. This is the G8 documented behaviour turned into an
executable statement, so a future "helpful" change to the token-less overloads has to argue with a
test.

**T3 — `Does_not_retry_or_respawn_when_the_invocation_is_cancelled`** — *the cancellation-specific
test the plan asks for.* Modelled on the existing
`Does_not_intercept_exceptions_that_are_not_node_invocation_failures`:

```
factory  : CountingFactory(_ => first ??= new FakeNodeInstance(_ => throw new TaskCanceledException()))
act      : await Assert.ThrowsAsync<TaskCanceledException>(() => services.InvokeAsync<string>("module"))
assert   : factory.CreateCount == 1      // no second instance → no retry
assert   : first.Disposed == false       // no teardown, no 15s draining window
```

Three assertions, each naming a distinct thing that must not happen. This is the test that makes
Spike 8's gate ("a cancelled RPC cannot be retried or mistaken for a dead node instance") a
standing guarantee instead of a one-time reading. Worth a `[Theory]` over
`TaskCanceledException` / `OperationCanceledException` so both cancellation shapes are covered.

**T4 — `Still_retries_when_a_node_invocation_failure_is_cancellation_shaped`** *(optional, one
`[Fact]`)*
`throw new NodeInvocationException("cancelled", "details", nodeInstanceUnavailable: true, allowConnectionDraining: false)`
still retries. Guards against a future "fix" that suppresses retries by sniffing for cancellation in
the wrong place.

### New file: `Tests/Prerendering/PrerendererCancellationTests.cs`

Needs a recording `INodeServices` — the existing `PrerenderingHarness.ExplodingNodeServices` throws
on all four members by design, so add a sibling `RecordingNodeServices` in the same harness class
that records `(CancellationToken token, string moduleName, string export, object[] args)` from the
**token-taking** `InvokeExportAsync` and **throws** from the token-less one. Throwing from the
token-less overload is the point: it makes overload-binding regressions fail loudly instead of
quietly recording `CancellationToken.None`.

**T5 — `Passes_the_supplied_token_to_the_node_rpc`** *(fails on `master`)*
Call `Prerenderer.RenderToString(...)` directly (reachable via `InternalsVisibleTo`) with a distinct
`cts.Token` as `requestCancellationToken` and assert `recorded.Token == cts.Token`. On `master` this
does not even compile (no such parameter); after M5 it is the direct proof that the token reaches the
RPC. This is the red→green test for G7.

**T6 — `Links_both_the_request_and_the_shutdown_token`** *(middleware level, via `PrerenderingHarness`)*
Run a request whose inner pipeline returns 200 `text/html`, with `RecordingNodeServices` and **no**
`ISpaPrerenderingService` status bail-out, so the middleware really reaches the RPC. Then, on the
recorded token:
- cancel `context.RequestAborted`'s source → recorded token is cancelled;
- in a second run, call `HarnessApplicationLifetime.StopApplication()` → recorded token is cancelled.

This asserts what `Prerenderer` cannot: that the **middleware** linked both parents (§2), not just
one. `DefaultHttpContext.RequestAborted` is settable (Spike 1 side finding), so no socket work is
needed. Note for the implementer: with the Defect 2 abort early-return in place, the token must be
cancelled *after* the middleware has entered the RPC, not before — have `RecordingNodeServices`
signal a `TaskCompletionSource` on entry and return `Task.Delay(Timeout.Infinite, token)`, then
cancel and await.

**T7 — `Propagates_the_cancellation_out_of_the_middleware`**
Same harness shape as T6; assert
`await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline(context))`. This is the
executable form of the release note in §7 — the one externally visible behaviour change W3 makes.

**T8 — `Does_not_delete_the_shared_node_script_when_the_request_token_is_cancelled`** — *the trap
regression test.* Call `Prerenderer.RenderToString` twice with an **already-cancelled**
`requestCancellationToken` and a live `applicationStoppingToken`, capture `recorded.ModuleName` from
both calls, and assert the two filenames are equal **and** `File.Exists(filename)`. On `master` this
passes trivially; its value is that it fails loudly the day someone "simplifies" the two tokens back
into one. Given that the collapsed version breaks prerendering permanently and only from the
*second* request onward, a cheap standing test is well spent.

### Coverage note

The PRD's success criteria require `…SpaServices.Prerendering` coverage to go up. `Prerenderer` is
currently at zero — no test calls it. T5/T6/T8 give it its first coverage, including the
`GetNodeScriptFilename` lock path, so W3 contributes to that criterion on its own.

## 7. Release notes

### `MintPlayer.AspNetCore.SpaServices.Prerendering/RELEASE-NOTES.txt` — under `BREAKING CHANGES`

The existing file already lists behaviour changes under `BREAKING CHANGES` for the current `v 10.6.0`
entry, which is the right heading: no API changes, but code that could not previously see an
exception now can.

```
- Prerendering is now cancelled when the client disconnects or the host shuts down. The request's
  cancellation token (HttpContext.RequestAborted linked with IHostApplicationLifetime.ApplicationStopping)
  is now passed to the Node.js invocation, which previously ran with CancellationToken.None and always
  ran to completion. A prerender that is abandoned this way surfaces as an OperationCanceledException
  from the prerendering middleware, where the middleware previously always ran to completion and
  returned a response. ASP.NET Core treats that as a client disconnect rather than an error, but any
  custom exception handling or logging around UseSpaPrerendering should expect it. Note that Node.js
  is not signalled: the render finishes on the Node.js side and its result is discarded. What this
  recovers is the .NET request thread, not Node.js CPU.
```

### `MintPlayer.AspNetCore.NodeServices/RELEASE-NOTES.txt` — under `FIXES`

Only if §4 is taken.

```
- A client disconnect that races the invocation timeout is no longer reported as a timeout. When both
  the caller's cancellation token and the invocation timeout fired, the caller received
  NodeInvocationException("The Node invocation timed out after …") advising them to check that their
  Node.js function invokes its callback - for what was in fact a cancelled invocation. The caller's
  token is now tested first, so a cancellation surfaces as an OperationCanceledException.
- The XML documentation on INodeServices now states that the overloads without a CancellationToken
  parameter invoke Node.js with CancellationToken.None, and that cancelling the token-taking overloads
  releases the calling thread without signalling Node.js. Behaviour is unchanged; the overloads
  without a token remain fully supported for invocations that are not scoped to a request.
```

## 8. Summary of decisions for M5

| # | Decision |
|---|---|
| 1 | Add required `CancellationToken requestCancellationToken` **last** on both `Prerenderer.RenderToString` overloads; no default; leave `applicationStoppingToken` untouched; keep the (dead) `HttpContext` overload consistent and non-magical. Verified: not a breaking change. |
| 2 | Link in the middleware, `using var prerenderCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStoppingToken)`, declared immediately before the render call; pass `requestCancellationToken:` as a named argument. `using var` is correct. Also fix `SpaProxy.cs:59`'s undisposed linked CTS (flagged as optional). |
| 3 | **Skip** `:140`, `:163` and `:283`. On aborted requests they are provable no-ops today, and a token would convert them into thrown exceptions — including on the dev-proxy path that routes through `:140`. `:283` would additionally break `SpaPrerenderingReflection`. |
| 4 | **Fix** the timeout/cancellation race in `OutOfProcessNodeInstance` (~3 lines: keep the caller's token, test it first). It changes the exception *type*, and W3 is what makes the branch reachable. No unit test — the class has no seam; stated in the PR rather than hidden. |
| 5 | Keep the token-less `INodeServices` overloads, no `[Obsolete]`; add the `<remarks>` drafted in §5 to all four overloads. |
| 6 | Tests T1-T3 (+optional T4) in `NodeServicesImplTests`; T5-T8 in a new `Tests/Prerendering/PrerendererCancellationTests.cs` with a `RecordingNodeServices` added to `PrerenderingHarness`. T3 is the no-retry/no-respawn test; T5 is the red→green proof the token reaches the RPC; T8 pins the temp-file trap. |
| 7 | Release-note lines drafted in §7 for both packages. |
