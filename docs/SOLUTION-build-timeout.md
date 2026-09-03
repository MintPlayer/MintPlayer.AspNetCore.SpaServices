# SOLUTION — the unwired SSR build timeout (`MintPlayer.AspNetCore.SpaServices.Prerendering`)

Scope: the dead `catch (OperationCanceledException)` at `AngularPrerendererBuilder.cs:81`, the unused
`buildTimeout` at `SpaPrerenderingExtensions.cs:62`, the unreferenced
`Prerendering/Extensions/TaskTimeoutExtensions.cs`, and the resulting "a hung `ng build --watch`
hangs the first request forever".

Decision document only. No production or test code is changed by this file.

---

## 0. Established facts — re-verified

All four hold as stated in the brief.

| Fact | Verdict |
|---|---|
| `AngularPrerendererBuilder.cs:81` `catch (OperationCanceledException)` is dead | ✅ Confirmed. The `try` awaits only `EventedStreamReader.WaitForMatch`; its TCS resolves solely via `tcs.SetResult(match)` (line-match handler) or `tcs.SetException(new EndOfStreamException())` (stream-closed handler), both inside `ResolveIfStillPending`. No `SetCanceled`/`TrySetCanceled` exists anywhere in the repo. |
| The catch was meant to be live | ✅ Its message reads "timed out without indicating success". |
| `buildTimeout` computed and never used | ✅ `SpaPrerenderingExtensions.cs:62`, zero further references. |
| `Prerendering/Extensions/TaskTimeoutExtensions.cs` unreferenced by production code | ✅ Only `Tests/Prerendering/PrerenderingInternalsTests.cs` (`PrerenderingTaskTimeoutExtensionsTests`) touches it. The sibling copy `SpaServices/Utils/TaskTimeoutExtensions.cs` is wired at `AngularCliMiddleware.cs:40-42`. |
| First request hangs forever | ✅ `isBuildStarted` latch + inline `await options.BootModuleBuilder.Build(spaBuilder)` at `SpaPrerenderingExtensions.cs:87-93`, and the awaited `WaitForMatch` is the package's only unbounded await. |

**One new finding, and one correction to a nearby assumption:**

1. **`SpaPrerenderingOptions.TimeoutMilliseconds` has a wrong XML doc.** It reads *"DEV: Max number of
   milliseconds to wait before the server bundle is built. Defaults to "0" (30s). "-1" means wait
   indefinitely."* It is in fact the **node RPC render timeout**: it is passed at
   `SpaPrerenderingExtensions.cs:176` as `timeoutMilliseconds` into `Prerenderer.RenderToString`,
   which forwards it as a *JavaScript argument* to `renderToString` in `prerenderer.js`. It has
   nothing to do with building the bundle. Anyone reading the options class today would reasonably
   believe the build timeout already exists and is configurable. This is a live trap for a
   consumer and it is directly in the way of question 6 below — the doc comment must be corrected
   as part of this work.
2. Consequently: **there is no existing build-timeout option**, wrongly-documented or otherwise.
   `StartupTimeout` is the only candidate.

---

## 1. Which timeout mechanism — **recommendation: option (c)**, `Task.WaitAsync(TimeSpan, CancellationToken)`

### The proposal

Inside `AngularPrerendererBuilder.Build`, bound the whole wait loop with the BCL's own combined
overload (available since .NET 6; this package targets `net10.0` only):

```
// buildTimeout read from spaBuilder.Options.StartupTimeout inside Build
for (var i = 0; i < finishedRegexIndex; i++)
{
    await scriptRunner.StdOut.WaitForMatch(finishedRegex)
        .WaitAsync(buildTimeout, applicationStoppingToken);
}
```

with the catch block re-shaped to three arms:

| arm | reached when | message |
|---|---|---|
| `catch (EndOfStreamException ex)` | npm/ng process exits, stream closes with no match | existing "…exited without indicating success" + stdout + stderr (**unchanged**) |
| `catch (TimeoutException ex)` | `buildTimeout` elapses with no match and no exit | existing "…timed out without indicating success" text, **moved here verbatim** + stdout + stderr |
| `catch (OperationCanceledException ex)` | `applicationStoppingToken` fires (host shutting down) | **new** "…was cancelled because the application is shutting down" + stdout + stderr |

`WaitAsync` is the whole mechanism. It is 1 line of production change plus a re-shaped catch block.

### Why (c) beats (a) and (b)

**Against (a) `WithTimeout` + change line 81 to `TimeoutException`.** This is the option that walks
straight into the trap. `WithTimeout` propagates faults via `task.Wait()`, which wraps in
`AggregateException` — pinned today by `PrerenderingInternalsTests.cs:238` and
`TaskTimeoutExtensionsTests.cs:50,61`. The `EndOfStreamException` would arrive as
`AggregateException{EndOfStreamException}`, would **not** match `catch (EndOfStreamException)` at
line 74, and the good "exited without indicating success" diagnostic — the one that reports npm's
stdout and stderr, i.e. the actual build error — would be silently destroyed. It is also the *more
common* failure in practice (a broken `ng build` exits; it rarely hangs), so (a) trades the rare
failure's diagnostic for the common one's. (a) would only be safe if paired with the question-2 fix
to `WithTimeout`, which makes it strictly more change than (c) for strictly less benefit — it still
gives no shutdown cancellation.

**Against (b) a linked `CancellationTokenSource(buildTimeout)` + a new
`WaitForMatch(Regex, CancellationToken)` overload.** I read `EventedStreamReader` to cost this out.
It is *not* very invasive — roughly 6 lines: register a callback on the token that calls the
existing `ResolveIfStillPending(() => tcs.TrySetCanceled(token))`, and dispose the
`CancellationTokenRegistration` inside `applyResolution` alongside the existing handler
unsubscription. The existing `completionLock`/`ResolveIfStillPending` design already makes this race-free,
which is a credit to it. Two real snags, neither fatal: the `token.Register` callback runs
*synchronously and inline* if the token is already cancelled, i.e. before the local `ctr` variable
is assigned, so the disposal has to be null-tolerant; and `CancellationTokenRegistration.Dispose()`
gets called from inside its own callback, which is documented not to deadlock but is the kind of
thing a reviewer has to stop and check.

So (b) is affordable, and it delivers exactly what the original author intended: a genuine
`OperationCanceledException` making line 81 live *as written*. I still reject it, for three reasons:

1. **It changes a shared internal primitive to serve one call site.** `EventedStreamReader` is
   duplicated in both packages (the header comment on `PrerenderingInternalsTests.cs:10` notes the
   Prerendering package carries its own copies) and is used by `NodeScriptRunner` for stdout/stderr
   plumbing in both. Widening its contract for one consumer is complexity pushed *up*, not down.
   `WaitAsync` is the framework already providing this composition, correctly, for free.
2. **(b) cannot distinguish timeout from shutdown without extra work.** With a linked CTS, the
   `TaskCanceledException.CancellationToken` is the *linked* token in both cases, so the catch has
   to interrogate `applicationStoppingToken.IsCancellationRequested` to pick a message — and if it
   does not, a `Ctrl+C` during a slow build reports "the npm script timed out", which is a false
   diagnostic. (c)'s combined overload separates the two into distinct exception types for free.
3. **(b) is a superset of (c)'s only real weakness.** See below.

**(c)'s one weakness, and why it is acceptable.** `WaitAsync` abandons the inner task rather than
cancelling it: the orphaned `WaitForMatch` keeps its `OnReceivedLine`/`OnStreamClosed` handlers
subscribed to the reader until the stream closes. That is a leak of two delegates, once per
`finishedRegexIndex` iteration, once per application lifetime, on a build that has already failed
fatally and is about to surface an `InvalidOperationException`. It is not worth a primitive change
to reclaim. (b) is the option that unsubscribes properly; if a future need makes cancellable
`WaitForMatch` worth having on its own merits, (b) is the right shape and this decision does not
block it.

**Rejected sub-variant (c2):** `WaitAsync(cts.Token)` with `cts = CreateLinkedTokenSource(applicationStoppingToken)`
+ `cts.CancelAfter(buildTimeout)`. This makes line 81 live as written with a real OCE and no
`EventedStreamReader` change — it is a perfectly good answer — but it lands back in snag 2 above
(timeout and shutdown are indistinguishable without token introspection). The combined
`WaitAsync(TimeSpan, CancellationToken)` overload is the same thing with the discrimination done by
the BCL.

### Where the timeout value comes from — delete line 62 rather than wire it

`buildTimeout` should be read **inside `AngularPrerendererBuilder.Build`**, from
`spaBuilder.Options.StartupTimeout`, which is already in scope there. The unused local at
`SpaPrerenderingExtensions.cs:62` should then be **deleted**, not wired.

Rationale: `ISpaPrerendererBuilder.Build(ISpaBuilder)` is a public interface, and the builder already
receives everything it needs through `spaBuilder`. Reading the option at the point of use needs
**zero public API change** and puts the timeout with the code that owns the rich diagnostic.

**Considered and rejected:** wrapping `await options.BootModuleBuilder.Build(spaBuilder)` at
`SpaPrerenderingExtensions.cs:87-93` in the timeout instead, so that *every*
`ISpaPrerendererBuilder` implementation is bounded — including third-party ones, which the inner fix
does not cover. It is tempting and it is the smaller diff. Rejected because the exception it can
throw is a bare `TimeoutException` with **no npm output**, which is precisely the diagnostic this
whole workstream exists to preserve; and doing both gives two nested timeouts of the same duration,
where whichever fires first is a race and the one that loses is the informative one. Bounding a
third-party builder's own internals is that implementer's responsibility.

### Adjacent: the `isBuildStarted` latch (flagging, in my area, recommend fixing here)

Once the build fails fast instead of hanging, a new behaviour becomes reachable that was previously
masked by the hang: `isBuildStarted` is set to `true` **before** the `await`
(`SpaPrerenderingExtensions.cs:87-93`), so after a timed-out or failed build, *every subsequent
request skips the build entirely* and proceeds to prerender against a missing or stale server
bundle — producing a confusing secondary error instead of the real one. The sibling package
explicitly designed against this; see the comment at `AngularCliMiddleware.cs:37-39`: *"On each
request, we create a separate startup task with its own timeout. That way, even if the first request
times out, subsequent requests could still work."*

Recommendation: replace the `bool` latch with a cached `Task` (`Interlocked.CompareExchange` on a
`Task?` field, or a `Lazy<Task>`) and, on failure, reset it so the next request retries — or at
minimum reset `isBuildStarted = false` in a `catch`/`finally` on the failure path so the real error
recurs rather than being replaced by a bundle-not-found error. This is in my scope (line 62 / 87-93)
and I recommend folding it in: it is 5 lines, and without it "fails with a clear error" is true only
of the *first* request.

---

## 2. Should `WithTimeout` itself be fixed to `await task`? — **yes, but as a decoupled cleanup**

**It is not load-bearing for my recommendation.** Option (c) does not use `WithTimeout` at all, so
this question is now independent, and it should be judged on its own merits rather than smuggled in.

On its own merits: **yes, fix it**, in `SpaServices/Utils/TaskTimeoutExtensions.cs` (the surviving
copy — see question 3):

```
if (task == await Task.WhenAny(task, Task.Delay(timeoutDelay)))
{
    await task;                    // was: task.Wait();
}
...
return await task;                 // was: return task.Result;
```

Why the new behaviour is right: the helper's one production call site is
`AngularCliMiddleware.cs:40-42`, wrapping `StartAngularCliServerAsync`. I traced what happens to a
fault there — nothing catches it (`SpaProxyingExtensions` / `SpaProxy` / `ConditionalProxyMiddleware`
have no `TimeoutException` or general handler on that path), so it propagates to the request and
becomes a 500. Today, if the Angular CLI startup faults — e.g. the informative
`InvalidOperationException` from `NodeScriptRunner.LaunchNodeProcess` with its "Ensure that 'npm' is
installed… Current PATH is…" text — the developer's first line reads **"One or more errors
occurred."** and the real message is one level down. `await task` surfaces the original exception
with its original stack. That is strictly better diagnostics, and it is the same principle as the
rest of this workstream.

Compatibility: the helper is `internal` in both assemblies, so there is no public-API concern. The
only callers are `AngularCliMiddleware` and the tests.

**Exactly which test assertions change** (both files were added in PR #76):

| File | Test | Change |
|---|---|---|
| `Tests/Utils/TaskTimeoutExtensionsTests.cs:44-52` | `Surfaces_a_faulted_task_rather_than_the_timeout` | `Assert.ThrowsAsync<AggregateException>` → `Assert.ThrowsAsync<InvalidOperationException>`; drop the `Assert.IsType<InvalidOperationException>(ex.InnerException)` line (or replace with `Assert.Equal("inner failure", ex.Message)`); delete the now-false comment *"The non-generic overload propagates via Task.Wait, which wraps in an AggregateException."* |
| `Tests/Utils/TaskTimeoutExtensionsTests.cs:54-62` | `Surfaces_a_faulted_generic_task_rather_than_the_timeout` | same substitution |
| `Tests/Prerendering/PrerenderingInternalsTests.cs:233-241` | `PrerenderingTaskTimeoutExtensionsTests.Surfaces_a_faulted_task_rather_than_the_timeout` | **deleted along with the whole class** — see question 3 |

Those assertions describe observed behaviour, not a contract: the #76 comment is explanatory
("propagates via `Task.Wait`, which wraps…"), documenting the wrapping as a mechanism, not asserting
that the wrapping is desirable. The four tests around them (pass-through, result, timeout message,
generic timeout) are unaffected — the timeout path is untouched, and `TimeoutException` still carries
the supplied message.

Why not simply live with the wrapping: because it is precisely the property that creates the trap in
question 1. Leaving a fault-wrapping helper in the repo, pinned by tests as if intentional, is what
made option (a) look safe. Fixing it removes the landmine for whoever next reaches for the helper.
(And it is why fixing it is *not* a licence to then choose option (a): (a) would still leave line 81
reachable only via `TimeoutException`, with no shutdown cancellation.)

---

## 3. The duplicate helper — **delete the Prerendering copy**

Delete:
- `MintPlayer.AspNetCore.SpaServices.Prerendering/Extensions/TaskTimeoutExtensions.cs`
- the `PrerenderingTaskTimeoutExtensionsTests` class in
  `Tests/Prerendering/PrerenderingInternalsTests.cs:197-241` (6 tests, character-for-character
  duplicates of `Tests/Utils/TaskTimeoutExtensionsTests.cs` bar two message strings)

Keep `MintPlayer.AspNetCore.SpaServices/Utils/TaskTimeoutExtensions.cs` — it has the one real caller.

Rationale: under option (c) the Prerendering copy has zero production callers **and zero prospective
callers**. Keeping it means keeping a dead 30-line file plus 6 tests that pin the behaviour of dead
code — which is exactly the configuration that produced this bug report's adjacent finding: an
unreferenced helper sitting next to an unused `buildTimeout` variable *looked* like the intended
mechanism, so nobody noticed the mechanism was never connected. Deleting it makes the absence
legible. Coverage is unaffected in the wrong direction: removing uncovered-purpose dead code and its
duplicate tests raises the ratio.

**Rejected: consolidate by sharing.** The two are `internal` in separate assemblies, so sharing means
either (i) a shared `<Compile Include="../..." Link="..."/>` source file — which adds a
cross-project file reference, an easy-to-miss build coupling, and a shared file that neither project
visibly owns; or (ii) making one `public` — which puts a `WithTimeout` extension method on the
public surface of a SPA-hosting package forever, for a 15-line helper, and creates a real
`Prerendering → SpaServices` API dependency where today there is only duplication. Both cost more
than a 30-line duplicate would have saved, and the duplication count drops to **one** copy anyway
once the dead file is gone, so there is nothing left to consolidate.

(Incidental tidiness: the Prerendering copy carries an Apache-2.0 header while the surviving copy
carries the MIT header used elsewhere in the repo. Deleting the Apache-headered one is also the
tidier outcome.)

**Note this does not apply to `EventedStreamReader`/`NodeScriptRunner`**, which are genuinely
duplicated across both packages and genuinely used in both. That duplication is out of scope here
and should not be touched by this change.

---

## 4. Preserving both diagnostics — verification

Both catches are reachable under the recommendation, and the third is new. Reached as follows:

| Failure mode | Path to the diagnostic |
|---|---|
| **Build exited without indicating success** (broken config, compile error, missing dependency) | `ng`/npm exits → the child stdout `StreamReader` returns `chunkLength == 0` in `EventedStreamReader.Run` → `OnClosed()` → `onStreamClosedHandler` → `ResolveIfStillPending(() => tcs.SetException(new EndOfStreamException()))`. `WaitAsync` **propagates a faulted inner task's exception unwrapped** (unlike `WithTimeout`'s `task.Wait()`), so the `await` throws `EndOfStreamException` and `catch (EndOfStreamException)` at line 74 matches exactly as today. Message unchanged, `stdOutReader.ReadAsString()` / `stdErrReader.ReadAsString()` unchanged — and the two `EventedStreamStringReader`s are still alive, because the `using` block has not exited. **This is the assertion the whole design turns on, and it is the regression test in §5.** |
| **Build timed out** (hung watcher, silent `ng build`, machine wedged) | `buildTimeout` elapses with the inner task still incomplete and `applicationStoppingToken` unsignalled → `WaitAsync` throws `TimeoutException` → new `catch (TimeoutException)` carrying the *existing* "timed out without indicating success" wording, plus the same stdout/stderr dump. Previously unreachable. |
| **Application shutting down mid-build** (new) | `applicationStoppingToken` fires → `WaitAsync` throws `OperationCanceledException` → `catch (OperationCanceledException)` at line 81 becomes live, with a *shutdown-specific* message. Note `NodeScriptRunner` already registers `Dispose` (kill entire process tree) on the same token, so the child is being killed at the same moment — this catch is what turns "mystery hang on Ctrl+C" into a clear message. |

Ordering matters: `TimeoutException` does not derive from `OperationCanceledException`, so the two
catches are independent and order-insensitive. `EndOfStreamException` is an `IOException`, likewise
independent. All three arms keep the identical
`$"…\nOutput was: {stdOutReader.ReadAsString()}\nError output was: {stdErrReader.ReadAsString()}"`
tail, wrapped in `InvalidOperationException` with the original as `innerException` — unchanged
contract for anything catching the outer type.

One wording fix to fold in: the timeout message should name the knob, e.g. *"…timed out after
{buildTimeout.TotalSeconds} seconds without indicating success. If your build is legitimately slower
than this, raise SpaOptions.StartupTimeout."* The sibling `AngularCliMiddleware.cs:42-45` already
states its timeout duration in its message; matching that is both consistent and the honest answer to
question 6.

---

## 5. Tests — the seam exists, and no npm or 120s wait is needed

**The seam.** `EventedStreamReader`'s constructor takes a plain `StreamReader`, it is `internal`,
and `MintPlayer.AspNetCore.SpaServices.Prerendering.csproj:41` grants
`InternalsVisibleTo MintPlayer.AspNetCore.SpaServices.Tests`. `PrerenderingInternalsTests.cs` already
does `new EventedStreamReader(new StreamReader(stream))` over an in-memory `GatedStream` (lines 20-121).
**No npm process is involved.** `NodeScriptRunner` is the part that cannot be tested — it calls
`Process.Start` in its constructor — and `AngularPrerendererBuilder.Build` constructs a
`NodeScriptRunner` directly, so `Build` as it stands is untestable.

**Required production seam (small, and it is the same change that makes the timeout injectable).**
Extract the `using`/`try`/`catch` body of `Build` into an `internal static async Task` — call it
`WaitForBuildToFinish` — taking `(EventedStreamReader stdOut, Regex finishedRegex, int finishedRegexIndex,
TimeSpan buildTimeout, CancellationToken applicationStoppingToken, Func<string> readStdOut,
Func<string> readStdErr, string pkgManagerCommand, string npmScript)`. `Build` then constructs the
runner and the two `EventedStreamStringReader`s and calls it. This is a pure extraction — no
behaviour change — and it is what lets a test pass `buildTimeout: TimeSpan.FromMilliseconds(50)`
instead of 120 seconds. Without it, there is no way to prove the fix without either spawning npm or
waiting out `StartupTimeout`; **with it, every arm is a sub-second unit test.**

Test-helper prerequisite: `GatedStream` is currently `private sealed` nested inside
`PrerenderingEventedStreamReaderTests` (line 138). It needs promoting to an `internal` test helper
(its own file, or a shared nested-class-free type) so the new tests can reuse it, and it needs a
variant that **never** produces data and **never** closes — the existing one blocks the first read on
a `SemaphoreSlim` until `Release()`, which is already exactly the "hung build" shape: simply never
call `Release()`.

Committed tests:

| # | Test | Setup | Asserts |
|---|---|---|---|
| T1 | `Times_out_when_the_build_never_reports_success` | `GatedStream` never released (or a stream whose `ReadAsync` awaits `Task.Delay(Infinite, ct)`); `buildTimeout: 50ms`; unsignalled stopping token | Throws `InvalidOperationException`; `ex.Message` contains "timed out"; `ex.InnerException` is `TimeoutException`. **This is the test that fails on `master` by hanging** — it must carry an xunit timeout (or `Assert.ThrowsAsync(...).WaitAsync(TimeSpan.FromSeconds(5))`) so the regression manifests as a failure, not as a stuck CI run. |
| T2 | `Reports_npm_output_when_the_script_exits_without_success` | `GatedStream("ERROR: something broke in ng build\n")`, released; regex that never matches; **generous** `buildTimeout` (30s) so the timeout cannot win the race | Throws `InvalidOperationException`; message contains "exited without indicating success" **and** the literal `"ERROR: something broke in ng build"`. **This is the anti-trap regression test** — it is the assertion that fails if anyone later "simplifies" the fix to `WithTimeout`, and its comment should say so explicitly. |
| T3 | `Reports_stderr_when_the_script_exits_without_success` | as T2 but the text arrives on the stderr reader | message contains the stderr text under "Error output was:" (guards the second `ReadAsString()`, which T2 alone does not) |
| T4 | `Reports_shutdown_rather_than_a_timeout_when_the_application_stops` | hung stream; generous `buildTimeout`; a `CancellationTokenSource` cancelled immediately | Throws `InvalidOperationException`; `ex.InnerException` is `OperationCanceledException`; message mentions shutdown and **does not** say "timed out" (this is the assertion that would fail under option (b)/(c2) without token introspection) |
| T5 | `Succeeds_when_the_match_arrives` | `GatedStream("Build at: 12:00\n")`, `finishedRegexIndex: 1` | completes, no throw (the happy path is otherwise untested at this level) |
| T6 | `The_timeout_covers_the_whole_multi_match_loop` | `finishedRegexIndex: 2`, a stream emitting one match then hanging; `buildTimeout: 100ms` | times out — pins that the budget is per-build, not per-iteration. Worth a committed test because the loop makes the alternative reading plausible. |

Not testable, and stated as such: `AngularPrerendererBuilder.Build` end-to-end, `NodeScriptRunner`
construction, and the interaction of the timeout with the `isBuildStarted` latch in the middleware
(that last one *is* reachable via the `SpikeHarnessTests` pipeline harness with a fake
`ISpaPrerendererBuilder` that throws — worth one test if the latch fix from §1 is folded in:
`A_failed_build_is_retried_on_the_next_request`).

---

## 6. Is 120s right? — **yes, reuse `StartupTimeout`; reject a new option**

Numbers: `SpaOptions.StartupTimeout` defaults to `TimeSpan.FromSeconds(120)` (`SpaOptions.cs:102`,
pinned by `SpaOptionsTests.cs:17`). Spike 5 measured an SSR build at ~35s warm. A cold CI machine
with no `.angular/cache` and cold `node_modules` file cache is realistically 2-3× that — call it
70-105s — so 120s is *adequate but not generous*. That matters less than it looks, for one reason:
`BootModuleBuilder` is documented as **development-only** ("This property should be left as `null` in
production applications", `SpaPrerenderingOptions.cs:13-18`), so this timeout binds developer
machines and CI e2e runs, never production traffic.

**Reject a new option**, for now:

- **`ISpaOptions` is the wrong place and would be breaking.** `ISpaBuilder.Options` is typed as the
  public `ISpaOptions` interface in the Abstractions package. Adding a member to it is a
  source-breaking change for any external implementer, for a dev-only knob.
- **`SpaPrerenderingOptions` is the right place *technically*** — it is a public class, so a new
  property is purely additive and non-breaking — **but it is the wrong place *practically* right
  now**, because that class already carries `TimeoutMilliseconds`, whose XML doc currently and
  wrongly claims to be the build timeout (finding §0.1). Adding a second, real build timeout beside
  a mis-documented one is a configuration trap with two plausible knobs and no way for a reader to
  tell them apart.
- **The need is already met.** Anyone whose cold build exceeds 120s can raise `StartupTimeout`
  today; coupling to the dev server is defensible because both answer the same question — "how long
  may a request wait for the SPA toolchain to become ready?" — and both are dev-only.
- **A timeout that is slightly too tight fails loudly with the npm output attached**, which is the
  entire point of this change; a timeout that is too generous fails slowly and silently, which is
  today's bug. Erring tight is the correct direction, and the message names the knob (§4).

Two things to do instead of a new option:

1. **Fix the `TimeoutMilliseconds` doc comment** to say what it actually is: the maximum duration, in
   milliseconds, passed to the Node prerendering call (`0` = the JS default, `-1` = no limit). This
   is required work, not a nicety — it is currently the single most misleading sentence in this
   package's public surface, and it is the reason someone could believe the build timeout already
   worked.
2. **Name the knob in the timeout message** (§4), so a legitimately-slow cold build is a 10-second
   fix for the developer who hits it.

If real-world reports later show CI builds exceeding 120s, the non-breaking escalation is a
`TimeSpan? BuildTimeout` on `SpaPrerenderingOptions` defaulting to `null` = fall back to
`spaBuilder.Options.StartupTimeout`. Recording that as the pre-agreed shape means the decision can be
deferred without being lost. It is not needed to land this fix.

---

## Summary of the recommendation

| # | Decision |
|---|---|
| 1 | Bound the wait with `.WaitAsync(buildTimeout, applicationStoppingToken)` (option **c**). Read `buildTimeout` from `spaBuilder.Options.StartupTimeout` **inside** `AngularPrerendererBuilder.Build`; **delete** the unused local at `SpaPrerenderingExtensions.cs:62`. Re-shape the catch into three arms: `EndOfStreamException` (unchanged), `TimeoutException` (existing "timed out" wording + duration + knob name), `OperationCanceledException` (new, shutdown). No change to `EventedStreamReader`. |
| 1b | Fold in the `isBuildStarted` latch fix so a failed build is retried rather than replaced by a bundle-not-found error on every later request. |
| 2 | Fix `SpaServices/Utils/TaskTimeoutExtensions.cs` to `await task` / `return await task`. Decoupled from decision 1 (option (c) does not use the helper) but worth doing: it un-wraps the informative npm-not-found error at the `AngularCliMiddleware` call site. Changes 2 assertions in `Tests/Utils/TaskTimeoutExtensionsTests.cs` (`AggregateException` → `InvalidOperationException`) and deletes one stale comment. |
| 3 | **Delete** `Prerendering/Extensions/TaskTimeoutExtensions.cs` and the duplicate `PrerenderingTaskTimeoutExtensionsTests` class. Do **not** share source or make either copy public. |
| 4 | All three failure modes produce an `InvalidOperationException` carrying npm stdout **and** stderr. `EndOfStreamException` stays reachable because `WaitAsync` propagates inner faults unwrapped — this is the property option (a) would have destroyed, and T2 is the committed test that guards it. |
| 5 | Extract an `internal static WaitForBuildToFinish(...)` taking the timeout and the token; promote `GatedStream` to a shared internal test helper; add T1-T6, all sub-second, no npm, no 120s wait. T1 needs a hard test-level timeout so the `master` regression fails rather than hangs. |
| 6 | Reuse `StartupTimeout` (120s). Reject a new option. **Required:** correct the wrong `SpaPrerenderingOptions.TimeoutMilliseconds` XML doc, and name the knob in the timeout message. Pre-agree `SpaPrerenderingOptions.BuildTimeout` (`TimeSpan?`, null = fall back) as the non-breaking escalation if 120s later proves tight. |
