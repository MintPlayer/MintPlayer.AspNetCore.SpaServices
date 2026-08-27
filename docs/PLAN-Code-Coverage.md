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
