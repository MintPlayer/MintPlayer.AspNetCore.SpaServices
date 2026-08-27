# PRD: Code Coverage with Upload to coverage.mintplayer.com

## Overview

Introduce code-coverage measurement to this repository and publish the results to
[coverage.mintplayer.com](https://coverage.mintplayer.com), replicating the setup added to
`MintPlayer/MintPlayer.Dotnet.Tools` in commit `f69b852` and corrected in PR #170 (`827a945`).

Coverage is collected by coverlet's VSTest collector during `dotnet test`, emitted as Cobertura
XML, and uploaded by the `MintPlayer/CodeCoverage/action@master` GitHub Action. The coverage
service — not the workflow — publishes the `coverage/project` and `coverage/patch` check runs.

## Problem Statement

This repository currently has **no test projects at all**. Every workflow runs a `Test` step, but
`dotnet test` finds no test projects and silently succeeds:

1. **No safety net** — six packable libraries ship to NuGet with zero automated verification.
2. **No visibility** — there is no measurement of what is or is not exercised.
3. **A misleading green check** — the `Test` step passing means nothing today, but reads as if it does.
4. **Regression risk is carried by review alone** — recent behavioural work (npm workspace
   resolution, child-process teardown, SPA build caching) is intricate, easy to break, and
   currently unverified.

Wiring up coverage against zero tests would upload a 0% report and make the problem *look*
measured without being measured. So this PRD covers both halves: the pipeline **and** a real test
project that makes the number mean something.

## Proposed Solution

Three independent pieces, delivered together:

1. **A test project** (`MintPlayer.AspNetCore.SpaServices.Tests`) with xunit, targeting the pure,
   deterministic logic in the libraries — route building/matching, XSRF helpers, npm workspace
   `node_modules` resolution, and options defaulting.
2. **Coverage collection** via `coverlet.collector` + a root `coverlet.runsettings`, run in the
   same Release build that CI already produced.
3. **Upload** to coverage.mintplayer.com from the `build-master` and `pull-request` workflows,
   guarded so that a coverage outage, a missing secret, or a fork PR can never fail the build.

### Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                     GitHub Actions (ubuntu-latest)                    │
├──────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  1. dotnet restore                                                    │
│                                                                       │
│  2. dotnet build --configuration Release --no-restore                 │
│                                                                       │
│  3. dotnet test --no-restore --no-build --configuration Release       │
│        --settings coverlet.runsettings                                │
│        --collect:"XPlat Code Coverage"                                │
│        --results-directory coverage                                   │
│     └── writes coverage/<guid>/coverage.cobertura.xml (one per proj)  │
│                                                                       │
│  4. MintPlayer/CodeCoverage/action@master                             │
│     ├── gzip each report + `git ls-files` as fileList                 │
│     ├── POST /api/uploads        (multipart, Bearer covt_ token)      │
│     └── POST /api/uploads/finish (skip the ~2 min debounce)           │
│                                                                       │
│  5. (master only) dotnet pack + push to NuGet/GPR                     │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
              coverage.mintplayer.com merges sessions, resolves
              paths against fileList, publishes check runs + badge
```

## Functional Requirements

### FR-1: Test Project

- **FR-1.1**: A single test project SHALL be added at `MintPlayer.AspNetCore.SpaServices.Tests/`.
- **FR-1.2**: It SHALL target `net10.0` with `LangVersion 14`, matching every other project.
- **FR-1.3**: It SHALL set `IsPackable=false` and `IsTestProject=true`.
- **FR-1.4**: It SHALL use xunit, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, and
  `coverlet.collector`, with `PrivateAssets=all` on the two that must not flow transitively.
- **FR-1.5**: It SHALL be added to `MintPlayer.AspNetCore.SpaServices.sln`.
- **FR-1.6**: Tests SHALL NOT require node, npm, a network, a database, or a real web server.
- **FR-1.7**: Tests SHALL be deterministic and safe to run in parallel on a clean CI runner.

### FR-2: Coverage Collection

- **FR-2.1**: Coverage SHALL be collected via the VSTest collector (`--collect:"XPlat Code Coverage"`),
  not via `coverlet.msbuild`.
- **FR-2.2**: The output format SHALL be Cobertura.
- **FR-2.3**: A `coverlet.runsettings` at the repo root SHALL pin the settings that affect
  server-side path resolution, each with a comment explaining why it is pinned.
- **FR-2.4**: `UseSourceLink` SHALL be `false`. SourceLink rewrites `filename` to
  raw.githubusercontent.com URLs; the server suffix-matches report paths against `git ls-files`,
  and a URL tail makes duplicated basenames ambiguous — which the server drops silently.
- **FR-2.5**: `ExcludeByFile` SHALL exclude `**/*.g.cs` and `**/*.Designer.cs`. Source-generated
  output under `obj/` has no git-tracked counterpart, so the server cannot resolve it and discards
  it; excluding it makes the local report and the server agree.
- **FR-2.6**: `DeterministicReport` SHALL be `false`, pinned rather than defaulted, because
  `build-master` runs `dotnet pack` with `ContinuousIntegrationBuild=true` in the same job — if
  that property ever moves into a `Directory.Build.props`, paths become `/_/…` and this must flip.
- **FR-2.7**: `ExcludeByAttribute` SHALL NOT be set. The common snippet is harmful:
  `CompilerGeneratedAttribute` removes every async method body, and Roslyn puts a synthetic
  `[Obsolete]` on `readonly ref struct`.

### FR-3: Test Step Correctness

- **FR-3.1**: Every `Test` step SHALL pass `--no-build --configuration Release`.
- **FR-3.2**: The rationale SHALL be recorded in a comment: `--no-restore` does **not** imply
  `--no-build`. Without `--no-build` the solution is rebuilt in **Debug**, coverage is measured on
  that build while `dotnet pack` ships the Release one, and — specific to this repo — the Debug
  rebuild triggers `DebugEnsureNodeEnv`, which runs `npm install` in both demo ClientApps.
- **FR-3.3**: This fix SHALL be applied to all three workflows, including `build-any` (which does
  not upload coverage) — the wasted Debug rebuild is a bug there too.

### FR-4: Upload

- **FR-4.1**: `build-master` and `pull-request` SHALL upload; `build-any` SHALL NOT (master and PR
  already cover every commit that matters; a feature-branch push would report a third build for
  the same commit).
- **FR-4.2**: The upload SHALL use `MintPlayer/CodeCoverage/action@master` with
  `url: https://coverage.mintplayer.com` and `token: ${{ secrets.COVERAGE_TOKEN }}`.
- **FR-4.3**: `disable-search: true` SHALL be set. With search on, a glob matching nothing silently
  falls back to auto-detection and uploads stray unparsable reports.
- **FR-4.4**: `finish: true` SHALL be set, closing the build immediately instead of waiting out the
  server's ~2-minute debounce.
- **FR-4.5**: `fail-ci-if-error: false` SHALL be set. A coverage-service outage or a missing token
  must never fail `build-master` before `dotnet pack` (blocking a NuGet release) or make a PR unmergeable.
- **FR-4.6**: Each upload SHALL be guarded by `hashFiles('coverage/**/coverage.cobertura.xml') != ''`,
  so a run that produced no report is a no-op rather than an upload of nothing.
- **FR-4.7**: On `build-master` the guard SHALL additionally be `always()`, so coverage from a
  failing test run is still recorded.
- **FR-4.8**: The PR upload SHALL be skipped for fork PRs
  (`github.event.pull_request.head.repo.full_name == github.repository`). Forks get no secrets, so
  the upload cannot work; a contributor's PR must not go red over a missing token.
- **FR-4.9**: The PR upload SHALL pass `base-sha: ${{ github.event.pull_request.base.sha }}` so the
  server can compute the patch coverage.
- **FR-4.10**: The PR upload SHALL additionally set `continue-on-error: true`.

### FR-5: Repository Hygiene

- **FR-5.1**: `.gitignore` SHALL ignore `/coverage/`, `coverage*.json`, `coverage*.xml`, `coverage*.info`.
- **FR-5.2**: `README.md` SHALL carry the coverage badge linking to the repo's coverage page.

### FR-6: Secret Provisioning

- **FR-6.1**: A repository secret named `COVERAGE_TOKEN` SHALL be created, holding a `covt_` upload
  token minted at coverage.mintplayer.com (account page → Upload tokens).
- **FR-6.2**: The repository must be known to the coverage server — i.e. the MintPlayer GitHub App
  installed on it — or a `covt_` upload returns **404** (unknown and unauthorized are
  deliberately indistinguishable, to avoid an existence oracle).
- **FR-6.3**: This is a **manual, human step**. It cannot be done from this PR, and until it is
  done the upload step logs a warning and the build stays green (per FR-4.5).

## Non-Functional Requirements

### NFR-1: Build Time

- **NFR-1.1**: Adding `--no-build` SHALL *reduce* CI time by removing a full second compile.
- **NFR-1.2**: The test suite SHALL run in well under a minute; no test may sleep for a fixed delay.

### NFR-2: Safety

- **NFR-2.1**: No coverage failure mode may fail a build (FR-4.5 through FR-4.8).
- **NFR-2.2**: No secret value may ever appear in a workflow log.
- **NFR-2.3**: Tests SHALL NOT write outside their own temp directories, and SHALL clean up.

### NFR-3: Honesty of the Number

- **NFR-3.1**: No assembly exclusion list SHALL be used to inflate the percentage.
- **NFR-3.2**: A low starting percentage is acceptable and expected. The number must be *true*
  before it is high.

## Technical Implementation

### `coverlet.runsettings` (repo root)

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <ExcludeByFile>**/*.g.cs,**/*.Designer.cs</ExcludeByFile>
          <UseSourceLink>false</UseSourceLink>
          <DeterministicReport>false</DeterministicReport>
          <SkipAutoProps>false</SkipAutoProps>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

(Shipped with the full explanatory comments from the reference repo; abbreviated here.)

### Test project packages

| Package | Version | Notes |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | 18.9.0 | |
| `xunit` | 2.9.3 | |
| `xunit.runner.visualstudio` | 4.0.0 | `PrivateAssets=all` |
| `coverlet.collector` | 10.0.1 | `PrivateAssets=all` |

Versions match the reference repo, which runs the same SDK (`10.0.100`).

### Denominator hygiene — checked, not applicable

The reference repo's largest correction was flipping `ReferenceOutputAssembly="false"` on
`ProjectReference … OutputItemType="Analyzer"` entries, which had made generator DLLs runtime
dependencies and added ~4200 unreachable lines to the denominator.

**This repo is not affected.** All eight generator references here are `PackageReference` with
`PrivateAssets="all"`, e.g.:

```xml
<PackageReference Include="MintPlayer.SourceGenerators" Version="10.13.0" PrivateAssets="all"
                  IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
```

There are no `OutputItemType="Analyzer"` project references anywhere. No change is needed, but the
check is recorded here so the next person does not have to redo it.

### Demo projects and the denominator

`Demo.Web`, `XsrfDemo`, `Demo.Data`, and `Demo.Dtos` are `IsPackable=false` sample apps. They are
not the product. Whether they land in the denominator is decided in Spike 3 (below) rather than
assumed.

## Spikes

Three questions are cheaper to answer by experiment than by argument. Each is time-boxed and has a
decision recorded in the plan.

### Spike 1: Does `--no-build` survive a test project being added to the solution?

`dotnet build --configuration Release` builds the whole solution including the new test project, so
`dotnet test --no-build` should find the test assembly already built. Verify locally that the exact
CI command sequence works from a clean `git clean -xfd`-equivalent state, and that no `npm install`
is triggered.

**Risk if wrong:** CI fails with "test assembly not found" or silently rebuilds in Debug.

### Spike 2: Does the demo projects' MSBuild node coupling break `dotnet test`?

`Demo.Web` and `XsrfDemo` import `nodeservices.targets`, whose `DebugEnsureNodeEnv` target hard-errors
when `node --version` fails. It is conditioned on `'$(Configuration)' == 'Debug'`, so a Release test
run should never reach it. Confirm by running the CI command locally and watching for npm activity.

**Decision to record:** whether `-p:EnableSpaBuilder=false` is needed as a belt-and-braces guard.

### Spike 3: What does the coverage denominator actually contain?

Run the pipeline locally, open the Cobertura XML, and enumerate which assemblies and files appear.
Confirm the six shipped libraries are present, and see whether the demo apps inflate the
denominator with untestable sample code.

**Decision to record:** leave the demos in (honest, low number) or exclude them (higher number,
but an exclusion list — which NFR-3.1 discourages). Default: leave them in unless they dominate.

## Testing Plan

The tests are the deliverable here, not an afterthought. Targets, in priority order — the concrete
list is finalised from the testability survey and recorded in the plan document.

### Tier 1 — pure logic, no fakes

- **Routing**: route template parsing, parameter substitution, URL generation, route matching
  including trailing slashes, casing, optional and catch-all parameters, and null/empty inputs.
- **NodeServices**: npm workspace `node_modules` resolution — the directory walk, `package.json`
  `workspaces` parsing, and the fallback when no workspace root exists. This is recent, intricate,
  and entirely deterministic.
- **Options/defaults**: any options class with defaulting or validation.

### Tier 2 — with fakes

- **Xsrf**: cookie and header behaviour driven through a `DefaultHttpContext`.
- Anything reachable with a stub `IServiceProvider`, `ILogger`, or `IOptions`.

### Explicitly out of scope

Process spawning, real proxying to a dev server, and prerendering against a live node process.
These need integration infrastructure that this PR does not build.

## Success Metrics

1. `dotnet test` runs a non-zero number of tests, all passing, on a clean CI runner.
2. No `npm install` occurs during the CI test step.
3. `coverage/**/coverage.cobertura.xml` exists after the test step.
4. The upload step succeeds once `COVERAGE_TOKEN` is provisioned, and warns without failing before that.
5. The badge renders and links to a report showing the six shipped libraries.
6. CI wall-clock time does not increase.

## Out of Scope

Genuinely not being done, as opposed to deferred to keep the diff small:

- **Branch protection on the `coverage/project` / `coverage/patch` checks.** A repo setting, not a
  code change, and it should not be enabled until a baseline exists.
- **A coverage threshold or gate.** Meaningless until there is a baseline to threshold against.
- **Integration tests that spawn node.** Needs a test harness this PR does not build.
- **Creating the `COVERAGE_TOKEN` secret.** Requires repo-admin access to GitHub and an account on
  the coverage service (FR-6.3).

## Open Questions

1. **Is the MintPlayer GitHub App installed on this repository?** If not, the first upload 404s.
   Resolution: try it; the workflow stays green either way (FR-4.5).
2. **Should `build-any` upload with `partial: true`?** Current answer: no — it would report a third
   build for the same commit. Revisit if feature-branch visibility is wanted.

## Appendix A: Upload contract summary

`POST https://coverage.mintplayer.com/api/uploads` — `multipart/form-data`, ≤50 MB,
`Authorization: Bearer covt_…`.

Fields: `repository` (must contain `/`), `commitSha` (≥7 chars; the PR **head** sha, not the merge
commit), `branch`, `pullRequestNumber`, `parentSha`, `runId`, `runAttempt`, `jobName`, `workflow`,
`eventName`, `flags`, `partial`, `baseSha`, `rootDir`, `fileList` (`git ls-files` output — what
report paths are suffix-matched against), and repeated `files` (each gzipped; the server sniffs
`1f 8b` magic bytes and the report format, which is never declared).

Returns `202 { buildId, sessionId }`. Uploads sharing `(repository, commitSha, runId, runAttempt)`
merge into one build as separate sessions with max semantics.

`POST /api/uploads/finish` — JSON `{ repository, commitSha, runId, runAttempt }`, closes the build.

The action handles gzip, `git ls-files`, up to 3 retries, and 429 back-off.

## Appendix B: Public URLs

- Repository report: `https://coverage.mintplayer.com/r/MintPlayer/MintPlayer.AspNetCore.SpaServices`
- Commit report: `…/r/{owner}/{repo}/c/{sha}`
- Badge SVG: `https://coverage.mintplayer.com/badge/MintPlayer/MintPlayer.AspNetCore.SpaServices.svg`
