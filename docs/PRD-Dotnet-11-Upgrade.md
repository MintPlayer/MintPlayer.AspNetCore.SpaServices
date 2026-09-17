# PRD: .NET 11, C# 15, and the 11.0.0-rc.1 package line

## Overview

Move the whole repository onto .NET 11 and C# 15, and ship the six packages as `11.0.0-rc.1`.

Unlike the `net9.0 → net10.0` bump ([`3d440e8`](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/commit/3d440e8), a straight
replace), the six shipped libraries **multi-target `net10.0;net11.0`**. This mirrors what
`MintPlayer/MintPlayer.AspNetCore.Tools#31` and `MintPlayer/MintPlayer.Dotnet.Tools#182` — both
squashed on 2026-09-17 — just did across the rest of the estate.

The reason to keep `net10.0` is not caution, it is the support calendar: **.NET 11 is STS and goes
out of support on 2028-11-09, five days *before* .NET 10 LTS (2028-11-14).** Dropping `net10.0`
would strand LTS consumers on a package line that dies first.

## Problem Statement

1. **The SDK pin blocks .NET 11 outright.** `global.json` pins `10.0.100` with no `rollForward`, so
   the .NET 11 SDK is refused before a single project is evaluated.
2. **The estate has already moved.** Dotnet.Tools shipped its `11.0.0-rc.1` line and AspNetCore.Tools
   its `11.0.1-rc.0` line. This repo is the straggler, and its two consumable dependencies
   (`MintPlayer.Pagination`, `MintPlayer.FolderHasher.Targets`) now have `11.0.0-rc.1` builds live on
   nuget.org.
3. **The version numbers have drifted apart.** Four packages sit at `10.7.1`, while Prerendering and
   Routing sit at `10.8.0-preview1` carrying unreleased work. Six packages that are released in
   lockstep should carry one version.
4. **`LangVersion` is pinned to `14` in eleven separate places.** There is no `Directory.Build.props`
   and no central package management, so every property and every package version is duplicated
   per project. Each bump costs eleven edits, which is why they drift.

## Proposed Solution

### Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Targeting, six libraries + Tests | `net10.0;net11.0` | .NET 11 STS dies before .NET 10 LTS; matches the sibling repos |
| Targeting, four Demo apps | `net11.0` only | They are hosts, not shipped packages — nothing consumes them |
| `LangVersion` | `14` → `15`, all 11 projects | Explicit everywhere, so it is an independent, revertable axis |
| Shipped package version | `11.0.0-rc.1` | Aligns with the Dotnet.Tools line; unifies the 10.7.1 / 10.8.0-preview1 split |
| `MintPlayer.SourceGenerators` | `10.13.0` → `10.22.0` | Stays on 10.x **by design** — see below |
| `MintPlayer.SourceGenerators.Attributes` | `10.13.0` → `10.20.1` | Same |
| `MintPlayer.Pagination` | `10.0.0` → `11.0.0-rc.1` | Published |
| `MintPlayer.FolderHasher.Targets` | `10.1.0` → `11.0.0-rc.1` | Published — **and carries a breaking rename** |
| EF Core / Extensions | `10.0.2` → `11.0.0-rc.1.26425.128` | Platform lockstep |
| SDK | `11.0.100-rc.1.26425.128` + `rollForward: latestFeature` | Matches AspNetCore.Tools#31 |

### The 11.0.0-rc.0 correction

The upgrade was requested as "lib versions 11.0.0-rc.0". **No `11.0.0-rc.0` exists anywhere.** The
two merged PRs produced two different labels:

- `MintPlayer.Dotnet.Tools#182` → **`11.0.0-rc.1`** (33 packages)
- `MintPlayer.AspNetCore.Tools#31` → **`11.0.1-rc.0`** (15 packages; **this repo consumes none of them**)

This PRD adopts `11.0.0-rc.1`.

### The generators stay on 10.x — this is correct, not an oversight

`MintPlayer.SourceGenerators` and `MintPlayer.SourceGenerators.Attributes` target `netstandard2.0`,
and PR #182 deliberately left all ten netstandard2.0 packages on the 10.x line. **There is no 11.x
and there will not be one.** They move within 10.x only, which additionally picks up the
`roslyn4.0|4.9/cs` → `roslyn5.0/cs` analyzer-folder collapse from #182 — the change that makes them
select correctly under the .NET 11 SDK's Roslyn.

Every one of the six libraries depends on these generators for `[Inject]`/`[PostConstruct]`/
`[NoInterfaceMember]`. A generator that fails to load does not error; it emits nothing, and the
build collapses into a wall of "type or member does not exist". This was the single largest
pre-identified risk, and Spike 1 retired it.

## Requirements

### Functional

- **FR-1.1** The six shipped packages SHALL target `net10.0;net11.0` and produce `lib/net10.0/` and
  `lib/net11.0/` folders in the nupkg.
- **FR-1.2** All 11 projects SHALL set `<LangVersion>15</LangVersion>`.
- **FR-1.3** The six shipped packages SHALL carry `<Version>11.0.0-rc.1</Version>`.
- **FR-1.4** `MintPlayer.AspNetCore.NodeServices` SHALL continue to pack its `buildTransitive/`
  payload — the three MSBuild files plus the four FolderHasher DLLs — unchanged in layout.
- **FR-1.5** The `ComputeFolderHashTask` MSBuild task SHALL load and compute a hash under the .NET 11
  SDK's MSBuild.
- **FR-1.6** `dotnet test` SHALL pass on **both** target frameworks.
- **FR-2.1** `global.json` SHALL pin `11.0.100-rc.1.26425.128` with `rollForward: latestFeature`.
- **FR-2.2** The three workflows SHALL set the same SDK version.

### Non-functional

- **NFR-1.1** No new NuGet source. Everything SHALL resolve from nuget.org. *(Verified — Spike 1.)*
- **NFR-1.2** The upgrade SHALL NOT change runtime behaviour of any shipped API. No source file in
  any of the six libraries is edited except `Targets/nodeservices.targets`.
- **NFR-1.3** Warning count SHALL NOT rise per-TFM against the `net10.0` baseline.

## Out of scope

These are real findings from the investigation, deliberately **not** being done here. They are
behavioural changes that do not belong in a framework bump, and each needs its own tests:

- `SpaRouteService.cs:408` — `context.Features.GetType().GetProperty("RawTarget")` reflects off the
  *runtime type of the feature collection*, unguarded and AOT-hostile. The safe form
  (`Features.Get<IHttpRequestFeature>()?.RawTarget`) is already used in the sibling package at
  `SpaPrerenderingExtensions.cs:713`. **This is a latent bug, not an upgrade blocker** — it survives
  the upgrade because the feature-collection type did not change in .NET 11.
- `SpaPrerenderingExtensions.cs:152-163` — swaps `Response.Body` without replacing
  `IHttpResponseBodyFeature`, so `BodyWriter`/`SendFileAsync` writers bypass the capture.
- `ProcessTracker.cs` — raw `DllImport` under `PublishAot=true` (SYSLIB1054), duplicated verbatim in
  two packages.
- Newtonsoft.Json as the node RPC serializer on two `PublishAot=true` packages.
- `SpaProxyTests.cs:61-73` — private-field walk over `HttpMessageInvoker`.
- Introducing a `Directory.Build.props` to collapse the 11-way duplication.
- xunit v2 → v3.

## Resolved: `rollForward` across the RC → GA boundary

AspNetCore.Tools#31 flags, as an unverified assumption, that `rollForward: latestFeature` will accept
GA `11.0.100` once it replaces the RC on a runner. **Verified here, and it holds.**

GA `11.0.100` does not exist yet, so the equivalent question was put to the .NET 10 SDKs installed
locally (`10.0.112`, `10.0.401`) using a prerelease pin for a version that is *not* installed:

| `global.json` | Resolved |
|---|---|
| `10.0.100-rc.1.25451.107` + `rollForward: latestFeature` | **`10.0.401`** |
| `10.0.100-rc.1.25451.107`, no `rollForward` (control) | **`10.0.112`** |

A prerelease pin rolls forward to a **GA** SDK — and the control shows even the default
(`latestPatch`) does so. Prerelease-vs-GA is not a barrier in the SDK resolver; the version is
ordered by SemVer, where `11.0.100` sorts above `11.0.100-rc.1.*`.

This is belt-and-braces anyway: CI uses `actions/setup-dotnet` pinned to the same exact version, so
the runner always has an exact match and never needs to roll forward at all. `rollForward` matters
only for a local developer who has GA installed but not the RC.
