# Plan: Code Coverage with Upload to coverage.mintplayer.com

Implementation plan for [PRD-Code-Coverage.md](./PRD-Code-Coverage.md).

Branch: `feature/code-coverage`. Single PR — the pipeline and the tests that make its number
meaningful land together.

## Milestones

### M1 — Coverage pipeline ✅ (commit `1c41bf0`)

| Change | File |
|---|---|
| Cobertura settings, with the server-path-resolution ones pinned + commented | `coverlet.runsettings` |
| Ignore report output | `.gitignore` |
| Upload + `--no-build` fix | `.github/workflows/build-master.yml` |
| Upload + `--no-build` fix, fork guard, `base-sha` | `.github/workflows/pull-request.yml` |
| `--no-build` fix only (no upload by design) | `.github/workflows/build-any.yml` |
| Badge in the existing empty "Code coverage" column | `README.md` |

**Done.** Note M1 alone would upload a 0% report — M3 is what makes it mean anything.

### M2 — Spikes ✅

| Spike | Result |
|---|---|
| **1.** Does `--no-build` work once a test project is in the solution? | **Yes.** `dotnet build -c Release` builds the test project too, so `dotnet test --no-build` finds the assembly. Verified after M3 landed. |
| **2.** Does the demo projects' node coupling break `dotnet test`? | **No.** Verified locally: `dotnet restore` + `dotnet build -c Release --no-restore` completes in ~27s, exit 0, **no `npm install`**. `DebugEnsureNodeEnv` is gated on `'$(Configuration)' == 'Debug'`, and CI now runs Release throughout. **Decision: `-p:EnableSpaBuilder=false` is NOT needed.** The demo `ClientApp`s are also reached only via `ProjectReference`, and the npm targets flow through the NuGet package's `build/` folder, not project references. |
| **3.** What is actually in the denominator? | **Only the six shipped libraries.** The demo apps do not appear at all — they are not referenced by the test project, so the test host never loads them and coverlet never instruments them. **Decision: no assembly exclusion list is needed**, so NFR-3.1 holds with nothing to argue about. Baseline measured below. |

#### Spike 3 baseline (local, Release)

Overall **21.0%** line rate, **16.2%** branch rate — 357 of 1696 lines, across 106 tests.

| Package | Rate |
|---|---|
| `…SpaServices.Xsrf` | 100.0% |
| `…SpaServices.Routing` | 86.9% |
| `…SpaServices` | 16.8% |
| `…NodeServices` | 15.2% |
| `…SpaServices.Prerendering` | 4.3% |
| `…SpaServices.Abstractions` | n/a (interfaces only, no executable lines) |

The shape is the point, not the total. Routing and Xsrf are high because their logic is reachable
through public API. The three low numbers are dominated by code that spawns or talks to a node
process, which is out of scope for unit tests (see the PRD). Raising those needs an injectable
process seam, not more tests against the current shape.

### M3 — Test project ✅

`MintPlayer.AspNetCore.SpaServices.Tests`, xunit, `net10.0`, `IsPackable=false`, added to the `.sln`.

Packages pinned to the same versions as the reference repo (same SDK): `Microsoft.NET.Test.Sdk`
18.9.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.0, `coverlet.collector` 10.0.1.

`InternalsVisibleTo` is added to the four libraries with `internal` surface worth testing. All are
unsigned, so no public key is needed. This changes no shipped behaviour.

Test areas, in descending value:

| Area | Target | Reachable via |
|---|---|---|
| Routing | Route tree construction and `Flatten` ordering | `ISpaRouteService` (public) |
| Routing | `GenerateUrl` — substitution, excess params → query string, all 8 overloads | public |
| Routing | `GetCurrentRoute` — matching, parameter extraction, query parsing | public + a `RawTarget` feature |
| Routing | `SpaRouteNotFoundException` on an unknown name | public |
| NodeServices | `NodeServicesImpl` retry / connection-draining state machine | fake `INodeInstance` |
| NodeServices | `NodeInvocationException` cross-field validation | IVT |
| NodeServices | `NodeServicesOptions` defaulting | public |
| SpaServices | `EventedStreamReader` chunk-splitting + ANSI stripping | IVT + `MemoryStream` |
| SpaServices | `SpaOptions` validation and copy-constructor | public + IVT |
| SpaServices | `TaskTimeoutExtensions` | IVT |
| Prerendering | `AngularPrerendererBuilder` constructor defaulting/validation | public |
| Xsrf | Cookie name/value/options, via a response feature that runs `OnStarting` | public |

### M4 — Verify and open the PR ✅

Ran the exact CI command locally from a clean state, inspected the Cobertura output for Spike 3, and
opened [PR #76](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/pull/76).

CI result on the PR: `build-any` and `pull-request` both pass in 40s. The upload was **accepted on
the first attempt** — no manual secret provisioning was needed after all — and the service published
`coverage/project` (neutral, 20.8%, no baseline yet) and `coverage/patch` (neutral, no coverable added
lines). The server''s 20.8% against 21.0% locally is the server dropping paths it cannot resolve
against `git ls-files`, which is exactly what the pinned `UseSourceLink=false` and `*.g.cs` exclusion
are there to minimise.

## Behaviours pinned deliberately

Several tests assert what the code **currently does**, where that looks like a bug. They are marked
in the source with a comment saying so. Pinning them means a future fix is a deliberate, visible
change rather than a silent one — and the PR description lists them so they can be triaged
separately.

1. **`SpaOptions` copy-constructor drops `StartupTimeout` and `CliRegexes`.** Every other property is
   copied. `UseSpaImproved` clones options so multiple `UseSpa` calls don't interfere, so a custom
   startup timeout is silently lost there.
2. **`Redirect` intends 301 but sends 302.** It sets `StatusCode = 301`, then registers an
   `OnStarting` callback calling `Response.Redirect(url)`, which overwrites the status with 302.
3. **Query strings are discarded on the empty (`home`) route.** `/?a=b` matches `home` and returns
   empty `QueryParameters`, while every other route parses them.
4. **Route paths are not regex-escaped.** A route path is interpolated straight into a `Regex`, so a
   route `a.b` matches the URL `/axb`.
5. **No URL encoding or decoding anywhere.** `GenerateUrl` emits parameter values verbatim, and
   `GetCurrentRoute` never decodes, so a round-trip through a value containing `/`, `&`, or a space
   is lossy.
6. **Duplicate query keys throw.** `?a=1&a=2` reaches a `ToDictionary` and throws `ArgumentException`.

Separately, and **not** covered by a test: `ProcessTracker` exists as two byte-identical copies, in
`SpaServices/Npm/` and `SpaServices.Prerendering/Internals/`. A test asserting they have not drifted
would have to read source files by walking up from the test assembly, which is fragile in CI, and a
behavioural test is impossible because merely loading the type creates a Windows Job Object and
subscribes to `ProcessExit` for the life of the test host. Recorded here as a known duplication for
whoever consolidates it.

## Risks

| Risk | Mitigation |
|---|---|
| ~~`COVERAGE_TOKEN` not yet provisioned~~ | **Did not materialise.** The secret already existed and the GitHub App was already installed; the first PR upload was accepted and the service published its check runs. The fail-soft guards (FR-4.5 through FR-4.8) remain, so a future outage still cannot fail a build. |
| The first upload's number looks embarrassingly low | Intended. NFR-3: true before high. |
| A pinned "current behaviour" test looks like an endorsement of a bug | Each is commented in-source and listed in the PR body. |

---

## M5 — Raising coverage (second pass)

The first pass proved the pipeline and gave it an honest number. 20.8% was too low to be useful, so
this pass targeted the largest blocks of uncovered lines, chosen by parsing the actual Cobertura
report rather than by guessing.

**20.8% → 47.9% lines, 16.2% → 43.8% branches. 106 → 288 tests**, still running in under a second.

| Package | Before | After |
|---|---|---|
| `…SpaServices.Abstractions` | 100.0% | 100.0% |
| `…SpaServices.Xsrf` | 100.0% | 100.0% |
| `…SpaServices.Routing` | 86.9% | 86.9% |
| `…SpaServices` | 16.8% | **55.8%** |
| `…NodeServices` | 15.2% | **36.5%** |
| `…SpaServices.Prerendering` | 4.3% | **32.1%** |

### What was added

| Area | Tests |
|---|---|
| `SpaProxy`, `ConditionalProxyMiddleware`, `SpaProxyingExtensions` | 45 |
| `SpaPrerenderingExtensions` helpers + guards | 32 |
| Static files, default-page middleware, `UseSpaImproved` | 30 |
| `NodeServicesOptions`, `StringAsTempFile`, `TaskExtensions`, `EmbeddedResourceReader` | 32 |
| Prerendering's duplicate `EventedStreamReader` / `TaskTimeoutExtensions` copies | 16 |

The duplicate-copies item is worth calling out: `SpaServices.Prerendering` ships byte-identical
copies of three files from `SpaServices`. They are distinct types in a distinct assembly, so the
existing tests over the originals did not cover a single line of them. Testing the copies was the
cheapest coverage in the repo, and it now also guards against the two copies drifting apart.

### Reflection over widening

Many of the best remaining targets are `private static`. They are reached with reflection helpers
rather than by widening them to `internal`, keeping the deliberate non-refactor from the first pass
intact: **no shipped code changed in this pass.** If reviewers prefer widening, that is a clean
standalone follow-up — it should not ride along with test additions.

### Deliberately still uncovered

- **WebSocket proxying** — needs an `IHttpUpgradeFeature` plus a real `ClientWebSocket.ConnectAsync`
  to a live endpoint, and `PumpWebSocket` polls on a 100 ms delay. Neither deterministic nor fast.
- **`ConditionalProxyMiddleware`'s post-proxy branch** and the terminal `Run` delegate — both build
  their own `HttpClient` in the constructor with no seam to inject a handler, so completing them
  needs a real server. The routing decisions around them are fully covered. **An `HttpMessageHandler`
  constructor overload would close this gap** if it is judged worth a production change.
- **`Prerenderer`** — every path funnels through a process-wide `static StringAsTempFile` that is
  never reset, so touching it leaks state across the whole run. Its URL-composition logic is
  duplicated in `GetUnencodedUrlAndPathQuery`, which *is* covered.
- **`OutOfProcessNodeInstance` / `HttpNodeInstance` construction, `NodeScriptRunner`,
  `AngularCliMiddleware`** — all launch node. Raising these needs an injectable process seam, which
  is a design change, not a test-writing exercise.
- **`ProcessTracker`** — loading the type creates a Windows Job Object and subscribes to
  `ProcessExit` for the life of the test host.

## Further bugs found and pinned in M5

Added to the six from the first pass. All pinned as current behaviour with an in-source comment; none
fixed, because each is a consumer-visible behavioural change deserving its own commit.

7. **`IsHtmlContentType` compares Ordinal**, so `TEXT/HTML` and `Text/Html; charset=utf-8` return
   false and prerendering is silently skipped. Media types are case-insensitive per RFC 9110. This
   is the most likely of the set to be biting someone in production.
8. **Content headers are silently dropped on bodiless requests** in `SpaProxy`. The fallback that
   adds a rejected header is only reached when `Content != null`, so a GET/HEAD/DELETE/TRACE
   carrying `Content-Type` loses it with no trace.
9. **Only 301 is treated as permanent** in `ServePrerenderResult` — a 308 is downgraded to 302,
   losing both permanence and method preservation.
10. **The `Globals` guard is only in the else-branch** — a result with both `RedirectUrl` and
    `Globals` is silently accepted, while the same `Globals` on a rendered page throws.
11. **`Html == null` reaches `Response.WriteAsync(null)`** and surfaces as
    `ArgumentNullException (text)` — useless diagnostics for "the prerenderer returned nothing".
12. **An empty `RootPath` throws two different exception types** depending on entry point:
    `InvalidOperationException` from `AddSpaStaticFilesImproved`, `ArgumentException` from
    `DefaultSpaStaticFileProvider.Initialize`.
13. **`EmbeddedResourceReader.Read` never names the resource it failed to find** — a missing
    resource surfaces as `ArgumentNullException (Parameter 'stream')`.
14. **`UseProxyToSpaDevelopmentServer` has no argument guards** — a null builder would be a bare
    `NullReferenceException`; a missing `IHostApplicationLifetime` yields a DI error that says
    nothing about SPA proxying.

---

## M6 — Fixing the bugs the tests found

The pins did their job: they held the behaviour still long enough to be looked at, and each fix
turned a red test into a green one. Ten of the fourteen are now **fixed**; the remaining four are
left pinned deliberately (below).

Coverage after the fixes: **48.6% lines, 45.5% branches, 291 tests.**

### Fixed

| # | Fix | Package |
|---|---|---|
| 1 | `SpaOptions` copy constructor now carries `StartupTimeout` and `CliRegexes` | SpaServices |
| 2 | `Redirect` sends a real **301** — it passed `permanent: true` instead of assigning a status code that `Response.Redirect` then overwrote with 302 | Routing |
| 3 | The empty (`home`) route now parses its query string like every other route | Routing |
| 6 | A duplicated query key takes last-one-wins instead of throwing `ArgumentException` | Routing |
| 7 | `IsHtmlContentType` compares the media type case-insensitively and tolerates OWS before `;` | Prerendering |
| 8 | `SpaProxy` forwards content headers on bodiless requests by attaching an empty content | SpaServices |
| 9 | `ServePrerenderResult` treats **308** as permanent alongside 301 | Prerendering |
| 10 | The `Globals` guard runs before the redirect branch, so it is no longer silently skipped | Prerendering |
| 11 | A null `Html` reports "prerendering returned no HTML" instead of `ArgumentNullException (text)` | Prerendering |
| 13 | `EmbeddedResourceReader` names the resource and assembly it could not find | NodeServices |
| 14 | `UseProxyToSpaDevelopmentServer` validates its arguments on all three overloads | SpaServices |

### Also fixed (the four that were initially held back)

These were first left pinned because each changes which URLs match or which exception a consumer
catches. They are now fixed too, which means this PR carries the whole set rather than leaving a
second round of breaking changes for later — one upgrade for consumers, not two.

| # | Fix | Package |
|---|---|---|
| 4 | Route paths are regex-escaped. Only `{placeholders}` become capture groups; literal text is escaped, so a route `a.b` no longer matches `/axb`, and a path containing `(` no longer throws at match time. | Routing |
| 5 | Parameter values are percent-encoded on generate and decoded on parse, so a generate/parse round-trip is lossless for values containing `/`, `&`, `?`, `%` or a space. Query keys and values are encoded too. | Routing |
| 12 | An empty `RootPath` throws `InvalidOperationException` from both entry points. | SpaServices |
| — | The query is split at the **first** `?` (RFC 3986 3.4), not the last, so a later `?` stays in the query instead of being captured into a route parameter. | Routing |

Two deliberate choices inside #5:

- **`+` is not read as a space when decoding.** `GenerateUrl` encodes a space as `%20`, so the
  round-trip is symmetric without it, and treating `+` as a space would corrupt a value that
  legitimately contains one.
- **A null parameter value now encodes to an empty string** rather than throwing
  `NullReferenceException` from `ToString()`.

### Version bumps

`build-master` pushes with `--skip-duplicate`, so **without a version bump these fixes would never
reach NuGet** — the push would silently skip every package as a duplicate. Bumped accordingly:

| Package | Version | Why |
|---|---|---|
| `…NodeServices` | 10.4.0 → **10.4.1** | Diagnostics only, no behavioural change |
| `…SpaServices` | 10.5.0 → **10.6.0** | Proxy header forwarding and options-clone behaviour change |
| `…SpaServices.Routing` | 10.4.0 → **10.5.0** | Redirects change from 302 to 301; query parsing changes |
| `…SpaServices.Prerendering` | 10.5.0 → **10.6.0** | Content-type matching, 308 handling, and a new guard |

`…SpaServices.Xsrf` and `…SpaServices.Abstractions` are unchanged and keep their versions.

### Upgrade notes for consumers

Three of these are visible behavioural changes rather than pure fixes:

1. **`ISpaRouteService.Redirect` now returns 301, not 302.** Browsers and CDNs cache permanent
   redirects, so a wrong redirect is much stickier than before. This is what the code always read as
   intending, but it is a real change in what gets sent.
2. **A `RenderToStringResult` carrying both `RedirectUrl` and `Globals` now throws.** Previously the
   `Globals` were silently dropped on the redirect path. An app relying on that silence will start
   seeing an exception — which is the point, but it will surface at runtime.
3. **Responses whose content type differs in case (`TEXT/HTML`) are now prerendered.** They were
   being passed through unrendered; anything downstream that assumed those responses skipped
   prerendering will now see rendered HTML.

### Upgrade notes, second set

The four fixes above are the most consumer-visible in the PR. In addition to the three already
listed:

4. **Generated URLs are now percent-encoded.** A caller that was encoding values *itself* before
   passing them to `GenerateUrl` will now get double-encoded output and must stop doing so. This is
   the single most likely thing to need a change on upgrade.
5. **Extracted route and query values are now decoded.** Code that was decoding
   `SpaRoute.Parameters` values itself must stop.
6. **A route path containing a regex metacharacter now matches literally.** Any route that was
   (knowingly or not) relying on `.` or `[]` behaving as a pattern will stop matching the URLs it
   used to. Route paths were never documented as patterns, so this is expected to affect nobody —
   but it is the change with the quietest failure mode, since the symptom is a 404 rather than an
   error.

Final state: **48.8% lines, 45.9% branches, 302 tests.** No behaviour is left pinned as
known-wrong-but-unfixed.
