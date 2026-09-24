# Plan: `XSRF-TOKEN` cookie hardening

Implementation plan for [PRD-Xsrf-Cookie-Hardening.md](./PRD-Xsrf-Cookie-Hardening.md)
([issue #85](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/85)).

Branch: `bugfix/xsrf-cookie-hardening`, from the merged `master` (`bb9452e`).

**One PR.** The middleware fix, the options surface, the spikes' outcomes, the rewritten tests, the
docs, the version bump and the Spark-side swap all land as one unit of work. The Spark change is in
a different repository and cannot merge before this package publishes — that is a *sequencing*
constraint, not a split (M9).

## Milestones

### M1 — Investigation ✅ Complete

Six agents in parallel, no production code written.

| Agent | Scope | Outcome |
|---|---|---|
| **A** | Package inventory, provenance, consumers | **Not a Microsoft import** — Visual Studio "Middleware Class" item template. Three commits ever; **the cookie line is unchanged since `cbe8006`, 2023-11-18**. Recovered the generated `[Inject]` constructor from the portable PDB. Found `README.md:253` documenting the missing flags as a decision, and `RELEASE-NOTES.txt` stuck at `v 10.2.2` against a shipping `11.0.0-rc.1`. |
| **B** | The issue's five framework claims, cited to `dotnet/aspnetcore` | Claims 1, 2, 4, 5 confirmed; **claim 3 downgraded** — a null `RequestToken` is unreachable with `DefaultAntiforgery`. Found three things the issue misses: the `X-Frame-Options: SAMEORIGIN` write, the **unguarded** `SaveCookieTokenAndHeader`, and that **TestServer rethrows rather than swallowing**. Confirmed .NET 10 and .NET 11 are byte-identical in every relevant file. |
| **C** | MintPlayer.Spark | Repo is cloned at `C:\Repos\MintPlayer.Spark`. Spark's cookie **is already the issue's proposed shape**, verbatim. `XsrfCookieFlagTests` runs over real HTTPS Kestrel, so `Secure = IsHttps` satisfies it. Found `XsrfMintingPlacementTests` — a measured A/B proving this package's placement beats Spark's. Found the `csrf-refresh` collision with any HTML gate. |
| **D** | Test infrastructure, coverage, Demo apps | **An Xsrf suite already exists** (7 tests) and the assembly is at **100% line / 100% branch** — the defects live in fully-covered code. No `TestServer`, no `WebApplicationFactory`, no real socket anywhere in the suite. Three near-duplicate fake response features; the Xsrf one fires **FIFO**, Kestrel is **LIFO**. `Demo/Xsrf` exists and self-hosts its Angular app. |
| **E** | Design space, prior art, `GetTokens` vs `GetAndStoreTokens` | **`GetTokens` is a trap** — sufficient only when the inbound cookie token is already valid, and that condition is unreadable from outside the assembly (`IAntiforgeryFeature` is `internal`). Established the `CookieBuilder.Build` route, that `UseCookiePolicy` *does* apply to `OnStarting` appends, and that .NET 11's `CsrfProtectionMiddleware` does not obsolete the package. |
| **F** | In-repo `OnStarting` interactions | **The cookie is safe from prerendering** — `Set-Cookie` is not in the drop-set and the callback runs after `DropTemplateHeaders`. But found the real conflict: PR #83 deliberately preserves `Cache-Control`, and this middleware overwrites it, last, on every response. Confirmed the body swap does not flip `HasStarted`. |

Three findings that changed the framing, all unprompted:

- **The bug is 100% covered.** The framing is not "untested code has a bug"; it is "the test suite
  pins the bug" — `Leaves_the_cookie_readable_by_script` would pass unchanged with `Secure` and
  `SameSite` still missing.
- **Two packages in this repo fight each other.** #83's `Cache-Control` preservation is undone by
  this middleware's callback. Nobody has seen it because no demo and no test combines them.
- **There is a live production failure today, independent of the issue's five claims.**
  `CheckSSLConfig` + `SecurePolicy.Always` + a TLS-terminating proxy = `InvalidOperationException`
  inside `OnStarting` = blank 500 on every request. Spark's own Traefik deployment is that shape.

### M2 — PRD + plan ✅

This document and the PRD.

### M3 — Spikes ⬜

Seven. Each produces a written answer in the PRD or a `docs/SOLUTION-*.md`, plus whatever throwaway
harness proves it. No production code until M6.

Both demos self-host and self-build their Angular app — `dotnet run` installs `node_modules` when
absent (`nodeservices.targets:62-77` `DebugEnsureNodeEnv`) and starts `ng serve` itself via
`spa.UseAngularCliServer`. **No separate `npm install` or `ng serve` step.** `Demo/Xsrf` needs no
database. Both demos bind `https://localhost:5001;http://localhost:5000`, so they cannot run
simultaneously without an override.

| # | Spike | Question | Vehicle |
|---|---|---|---|
| **1** | **Is defect 4 reproducible, and is it testable at all?** | Force a throw inside the callback and observe the real wire response under Kestrel. Confirm the bare 500 with stripped headers, confirm `UseExceptionHandler` cannot see it, and decide whether the repo takes on a real-Kestrel test host or accepts unit-level assertions on the *degradation* only. | `dotnet run --project Demo/Xsrf/XsrfDemo.csproj`, plus a throwing `IAntiforgery` registered over the real one |
| **2** | **⚠️ Defect C, end to end** | `AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always)` + a plain-HTTP request. Confirm the `InvalidOperationException` → blank 500 → every header stripped, including `Set-Cookie`. This is the strongest evidence for the try/catch and belongs in the PR description. | `Demo/Xsrf` over `http://localhost:5000` |
| **3** | **Cookie flags on the wire, both schemes** | Real `Set-Cookie` observed on `http://localhost:5000` and `https://localhost:5001`: does `SameAsRequest` behave as predicted, does `SameSite` land, does the Angular app's `POST /WeatherForecast` still succeed on both? Also settle E's unverified item — does a `__Host-` prefixed cookie survive on `http://localhost`? | `Demo/Xsrf` + `curl -i`, plus the `playwright_node` MCP for the Angular round trip |
| **4** | **⚠️ Would a mint gate break Spark?** | `POST /spark/auth/csrf-refresh` returns a non-HTML `Results.Ok()` and depends on the mint. Establish whether *any* gate signal (`Sec-Fetch-Dest: document`, `Accept: text/html`, content type) keeps it working, or whether un-gated is the only safe default. Resolves Open decision 2. | Read-only analysis of `C:\Repos\MintPlayer.Spark` + a local run of Spark's `XsrfMintingPlacementTests` |
| **5** | **The `Cache-Control` conflict, demonstrated** | Build the pipeline that exists nowhere today — `UseSpaPrerendering` **and** `UseAntiforgeryGenerator` in one app — and show the preserved `Cache-Control` being overwritten. Then verify the chosen repair (Open decision 4) does not leave a shared-cacheable response carrying `Set-Cookie`. | `PrerenderingHarness.Run(configureUpstream: …)` + `Demo/Prerendering/Demo.Web` with the middleware added |
| **6** | **Options surface + AOT** | Settle the O3 shape: `UseAntiforgeryGenerator(Action<XsrfOptions>)` overload vs an `AddXsrf()` registration; whether `CookieBuilder.Build(httpContext)` is the right construction route; and that `<PublishAot>true</PublishAot>` still publishes clean. Resolves Open decision 3. | `dotnet publish -r win-x64 /p:PublishAot=true` on a scratch consumer |
| **7** | **LIFO test fidelity** | The Xsrf suite's `RunnableResponseFeature` fires **FIFO**; Kestrel and `PrerenderingTestContext.CallbackFiringResponseFeature` are **LIFO**. Confirm the Xsrf suite cannot observe the clobber today, and move it onto a LIFO fake. | `MintPlayer.AspNetCore.SpaServices.Tests/Xsrf/AntiforgeryMiddlewareTests.cs` |

**Deliverable.** `docs/SOLUTION-xsrf-onstarting-failure.md` (spikes 1-2),
`docs/SOLUTION-xsrf-cookie-policy.md` (spikes 3, 6), `docs/SOLUTION-xsrf-cache-headers.md`
(spikes 4-5), and the answers written back into the PRD.

### M4 — Resolve the seven open decisions ⬜

A design interview with @PieterjanDeClippel, informed by M3. The PRD's *Open decisions* table is
the authoritative record and gets filled in with the reasoning, exactly as
`PLAN-Prerendering-Response-Headers.md` did for its four spikes.

Gate: **no production code before decisions 1, 2, 3 and 4 are settled.** Decision 5 (drop the
`[Inject]` generator) and 7 (version) can be settled during M6. Decision 6 (the Spark swap)
determines whether M9 exists.

### M5 — Reproduction tests, red before green ⬜

Written against current code and **verified red** before any fix lands. Extends
`MintPlayer.AspNetCore.SpaServices.Tests/Xsrf/AntiforgeryMiddlewareTests.cs`. Record the failing
count before M6 and the passing count after — red-before-fix verified, not assumed.

First, **move the suite onto a LIFO response feature** (spike 7), or tests 9-11 pass for the wrong
reason.

**Cookie attributes**

| # | Case | Expected after fix |
|---|---|---|
| 1 | **Headline.** HTTPS request | `Set-Cookie` contains `secure` |
| 2 | Plain-HTTP request, default policy | no `secure` — plain-HTTP dev keeps working |
| 3 | `SecurePolicy = Always`, plain-HTTP request | `secure` present |
| 4 | **Headline.** Default configuration | explicit `samesite=` attribute present, matching Open decision 1 |
| 5 | `SameSite` configured to a non-default | that value emitted |
| 6 | Cookie name / path configured | honoured; defaults remain `XSRF-TOKEN` and `/` |
| 7 | `UseCookiePolicy` with `Secure = Always` upstream | cookie upgraded to `secure` — proves the append still flows through `ResponseCookiesWrapper` |
| 8 | Regression guard | `httponly` still **absent** — Angular must be able to read it |

**Failure degradation**

| # | Case | Expected after fix |
|---|---|---|
| 9 | `IAntiforgery` returns a token set whose `RequestToken` is `null` | no cookie, no throw, one log entry |
| 10 | **Headline.** `IAntiforgery` throws `InvalidOperationException` (defect C's shape) | callback completes, response otherwise intact, **no cookie**, one logged **error** carrying the exception |
| 11 | `IAntiforgery` throws `CryptographicException` (the DataProtection shape — note the homogenised type, not the inner `IOException`) | same |
| 12 | A second `OnStarting` callback registered *after* this one | still runs — proves the catch stops the LIFO chain from being abandoned |
| 13 | Response already started when the callback runs | no throw escapes |

**Cache headers**

| # | Case | Expected after fix |
|---|---|---|
| 14 | **Headline.** Upstream sets `Cache-Control: no-store` | survives the mint |
| 15 | Upstream sets `Cache-Control: public, max-age=60` on a response that mints | per Open decision 4 — **not** restored verbatim; downgraded, never shared-cacheable alongside `Set-Cookie` |
| 16 | Upstream sets `Pragma` | survives |
| 17 | Nothing upstream sets either | unchanged from today's behaviour |
| 18 | **Integration.** `UseSpaPrerendering` + `UseAntiforgeryGenerator` in one pipeline | the `Cache-Control` #83 preserves still reaches the client, **and** the cookie is present exactly once |

**Existing tests to change, each with a comment citing #85**

| Test | Change |
|---|---|
| `Leaves_the_cookie_readable_by_script` | keep, and extend to assert the *presence* of `secure`/`samesite` so it can no longer pass on a naked cookie |
| `Scopes_the_cookie_to_the_site_root` | keep; add the configured-path case |
| `Writes_no_cookie_until_the_response_starts` | keep verbatim — it pins the placement that is the package's whole advantage over Spark |
| `UseAntiforgeryGenerator_registers_the_middleware` | extend to actually run a request through the built pipeline, so `UseMiddleware`'s DI path is exercised (it is not today) |

### M6 — Implementation ⬜

Branch `bugfix/xsrf-cookie-hardening`.

**`AntiforgeryMiddleware.cs`**
- Static lambda with a tuple state; delete the dead `var context = (HttpContext)state;` and the
  commented-out path check that has been dead since 2023.
- `try`/`catch` around the body, logging an **error** with the exception. Never a bare `catch {}` —
  the PRD's risk table turns on this.
- Null guard on `RequestToken`, with a comment recording that it is insurance against a replaced
  `IAntiforgery`, **not** a reachable framework behaviour — so nobody later "simplifies" it away on
  the grounds that the framework never returns null, and nobody cites it as a security fix.
- Cookie built per Open decision 3, via `CookieBuilder.Build(httpContext)` if spike 6 confirms.
- Cache-header handling per Open decisions 2 and 4.
- One-time warning when the cookie is written non-`Secure` on a non-development host, naming
  `UseForwardedHeaders` and `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.
- Method injection (`Invoke(HttpContext, IAntiforgery)`) if Open decision 5 says yes — which means
  a hand-written constructor and dropping `[Inject]` for this type.

**`XsrfOptions`** (if Open decision 3 says yes) — plain POCO, no reflection binding, AOT-safe.

### M7 — Documentation ⬜

- **`README.md:253`** — replace the "framework's cookie policy defaults apply" sentence. Nothing
  applies unless the app calls `UseCookiePolicy`.
- **New section: what this package does *not* protect.** `AntiforgeryOptions.Cookie.SecurePolicy`
  defaults to `None`, so the framework's own antiforgery cookie ships without `Secure` even over
  HTTPS. Show the `CookieSecurePolicy.Always` fix and warn that it triggers defect C behind an
  unconfigured proxy.
- **New section: .NET 11.** Same-origin apps get a free `Sec-Fetch-Site` layer from
  `CsrfProtectionMiddleware`; JSON endpoints get no behavioural change, so this package is still
  required. **Do not cite** the contradicted "cross-origin SPAs can skip the token system" bullet.
- **Angular version note.** ≤ 20.0.x skips *any* absolute URL, including same-origin;
  ≥ 21.0.x compares origins. "Use relative API URLs" is the only version-independent advice.
  `Demo/Xsrf/ClientApp` is Angular 21.
- **`RELEASE-NOTES.txt`** — new section at the top for the shipping version (Open decision 7).
- **`<Version>`** bumped; `build-master` pushes with `--skip-duplicate`, so an unbumped package is
  silently skipped.

### M8 — Verify ⬜

- Full suite, both TFMs, batched: `dotnet test --settings coverlet.runsettings --collect:"XPlat Code
  Coverage"`, redirected to a log file rather than piped.
- Coverage: project ≥ 80%, patch ≥ 80%, `Xsrf` assembly not below 100%.
- **Reverse check** — revert the fix and confirm the M5 tests go red, so the suite is a real
  regression guard and not a restatement of the implementation.
- Success criteria 2 and 3 re-run against the real demo on both schemes.

### M9 — Spark swap ⬜ *(gated on Open decision 6 and on the publish)*

Cannot merge before this package publishes. Planned here, landed immediately after — not deferred.

- Delete the mint at `SparkMiddleware.cs:348-369`; call `UseAntiforgeryGenerator()` in its place,
  at the same pipeline position.
- Verify `XsrfCookieFlagTests` stays green (`Secure` + `SameSite=Strict` over the HTTPS fixture).
- Verify `SparkClient.EnsureAntiforgeryAsync` and `SparkEndpointFactory.MintAntiforgeryAsync` still
  find **both** cookies on the `GET /spark` warmup.
- Verify `csrf-refresh` still mints — the collision from spike 4.
- Re-run `XsrfMintingPlacementTests`: sign-in should now be **200 on the real package**, which is
  the measured upgrade.
- Update Spark's two written refusals (`docs/coverage-handoff-plan.md:667-678`,
  `docs/xsrf_minting_PRD.md` §4) to record that the package was hardened and adopted.

### M10 — PR ⬜

Against `master`. Description leads with spike 2's reproduction — the blank 500 behind a proxy is
the finding that justifies the whole change, and it is not in the issue.

## Decisions taken

*(To be filled in during M4. The PRD's* Open decisions *table is the authoritative record.)*

## Notes for whoever picks this up

- **Batch the test runs.** Verify intermediate milestones by reading the code and type-checking;
  one sweep at M8.
- **Redirect long output to a file**, unfiltered, then grep the file. A piped `dotnet test` reports
  `grep`'s exit code, not the test run's.
- `MintPlayer.Spark` is cloned at `C:\Repos\MintPlayer.Spark` — no need to fetch it.
- Do not use the `dcg:playwright` skill for the browser checks in spikes 1-3; use the
  `playwright_node` MCP tools directly.
