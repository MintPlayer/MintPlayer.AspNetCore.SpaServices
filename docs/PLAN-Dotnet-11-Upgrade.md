# Plan: .NET 11, C# 15, and the 11.0.0-rc.1 package line

Implementation plan for [PRD-Dotnet-11-Upgrade.md](./PRD-Dotnet-11-Upgrade.md).

Branch: `spike/dotnet-11`. **Single PR** — the framework bump, the two upstream breaks it exposes,
and the CI/doc updates land together.

## Spikes

All four ran against the locally installed SDK `11.0.100-rc.1.26425.128` /
`Microsoft.AspNetCore.App 11.0.0-rc.1.26425.128`, on branch `spike/dotnet-11`.

### Spike 1 — Does the solution build on the .NET 11 RC SDK? ✅ **Yes**

`dotnet restore` → **exit 0**, `dotnet build -c Release` → **exit 0**, `dotnet test` → **381 passed,
twice** (once per TFM), `dotnet pack` → **exit 0, six nupkgs** (after the Spike 2/4 fixes below).

Three results worth recording:

- **No new NuGet source is needed.** `nuget.config` lists nuget.org only, and every version resolves
  from it: the MintPlayer `11.0.0-rc.1` packages, the generators' 10.x builds, and the Microsoft
  `11.0.0-rc.1.26425.128` packages. NFR-1.1 holds.
- **The source generators load and run.** They emit `MP001` (unused using) and `INTF001` (public
  member not on the interface) — diagnostics that only appear *because* the generator is running.
  The single largest pre-identified risk is retired.
- **`NU1510` on Demo.Data.** `Microsoft.Extensions.DependencyInjection.Abstractions` is now in the
  shared framework on `net11.0`, so the explicit `PackageReference` is redundant. This is the same
  root cause that produced 120 errors in Dotnet.Tools#182; here it is only a warning, because
  Demo.Data leaks no compile reference. **Fix: delete the line.**

Warning counts are exactly doubled versus the `net10.0` baseline (two TFMs), with no new codes
beyond `NU1510` — NFR-1.3 holds. No `#if` directives exist anywhere in the repo, so the TFM change
alters no code path.

### Spike 2 — Does the FolderHasher MSBuild task still load? ⚠️ **Only after a rename**

**`MintPlayer.FolderHasher.Targets` 11.0.0-rc.1 renamed its task assembly and its task type.** This
is an undocumented breaking change and it is silent at build time — nothing references the task
during a Release build of this repo, so the failure would only surface in a consumer's Debug or
publish build.

| | 10.1.0 | 11.0.0-rc.1 |
|---|---|---|
| Assembly | `MintPlayer.FolderHasher.MSBuild.dll` | `MintPlayer.FolderHasher.Targets.dll` |
| Task type | `MintPlayer.FolderHasher.MSBuild.ComputeFolderHashTask` | `MintPlayer.FolderHasher.Targets.ComputeFolderHashTask` |

Both are referenced in `Targets/nodeservices.targets:4-5` and all four `<None>` pack items.

**Verified end-to-end after the fix**, by invoking the target directly (a Release build does *not*
exercise it):

```
dotnet msbuild Demo/Xsrf/XsrfDemo.csproj -t:ComputeSpaFolderHash \
  -p:Configuration=Release -p:EnableSpaBuilder=true -p:EnableSpaBuildCaching=true
→ Computed folder hash for 'ClientApp\': 038ed934…3708c    0 Error(s)
```

Also checked: the task does **not** need `Microsoft.Extensions.DependencyInjection.Abstractions.dll`
(present in the package's `build/` but deliberately not packed). Removing it and re-running still
succeeds, so the four-DLL pack set stays correct.

### Spike 3 — WebMarkupMin and `MapStaticAssets` in the demos ✅ **Both survive**

The `net9.0 → net10.0` bump's *only* source change was a WebMarkupMin namespace move
(`AspNetCore8` → `AspNetCoreLatest`). **There is no equivalent this time.**
`WebMarkupMin.AspNetCoreLatest` 2.20.0 compiles unchanged against `net11.0`, and
`endpoints.MapStaticAssets()` is intact in both `Demo/Prerendering/Demo.Web/Startup.cs` and
`Demo/Xsrf/Startup.cs`. **Zero C# source changes in the entire upgrade.**

### Spike 4 — NU5118 / pack layout under multi-targeting ❌ **Real break, now fixed**

`dotnet pack` failed with **exit 1** and produced only five of six packages:

```
NuGet.Build.Tasks.Pack.targets(232,5): error : Could not find a part of the path 'C:\build'.
  [MintPlayer.AspNetCore.NodeServices.csproj]
```

Not NU5118 as predicted — a different and more interesting failure. `GeneratePathProperty="true"`
emits `$(PkgMintPlayer_FolderHasher_Targets)` into `obj/*.nuget.g.props` inside **TFM-conditioned**
property groups:

```xml
<PropertyGroup Condition=" '$(TargetFramework)' == 'net10.0' AND … ">
  <PkgMintPlayer_FolderHasher_Targets>…\11.0.0-rc.1</PkgMintPlayer_FolderHasher_Targets>
```

`dotnet pack` collects `<None Pack="true">` items in the **outer, cross-targeting build**, where
`$(TargetFramework)` is empty. So the property expands to nothing and the path collapses to
`\build\…dll` → `C:\build`. **Single-targeting hid this; multi-targeting exposes it.**

**Fix:** stop using the path property for TFM-independent build assets. Introduce a
`$(FolderHasherTargetsVersion)` property (also used by the `PackageReference`, so the version is
stated once) and compose the path from `$(NuGetPackageRoot)`, which is defined in the outer build.

Verified — the packed layout is correct:

```
lib/net10.0/MintPlayer.AspNetCore.NodeServices.dll
lib/net11.0/MintPlayer.AspNetCore.NodeServices.dll
buildTransitive/MintPlayer.AspNetCore.NodeServices.props
buildTransitive/MintPlayer.AspNetCore.NodeServices.targets
buildTransitive/npm-install.proj
buildTransitive/MintPlayer.FolderHasher.Targets.dll
buildTransitive/MintPlayer.FolderHasher.dll
buildTransitive/MintPlayer.FolderHasher.Abstractions.dll
buildTransitive/Microsoft.Extensions.FileSystemGlobbing.dll
```

## Milestones

### M1 — Framework, language and version bump ✅

| Change | Files |
|---|---|
| `<TargetFramework>net10.0</TargetFramework>` → `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>` | 6 libraries + Tests |
| `<TargetFramework>net11.0</TargetFramework>` | 4 Demo apps |
| `<LangVersion>14</LangVersion>` → `15` | all 11 |
| `<Version>` `10.7.1` ×4 / `10.8.0-preview1` ×2 → `11.0.0-rc.1` | 6 libraries |
| `MintPlayer.SourceGenerators` → `10.22.0`, `.Attributes` → `10.20.1` | 8 projects |
| `MintPlayer.Pagination` → `11.0.0-rc.1` | Demo.Data |
| EF Core / Extensions `10.0.2` → `11.0.0-rc.1.26425.128` | Demo.Data, Demo.Web |
| SDK pin + `rollForward: latestFeature` | `global.json` |

### M2 — Upstream breaks exposed by the bump ✅

| Change | File |
|---|---|
| FolderHasher task + assembly rename | `MintPlayer.AspNetCore.NodeServices/Targets/nodeservices.targets:4-5` |
| Same rename in the four pack items; `$(Pkg…)` → `$(NuGetPackageRoot)` + `$(FolderHasherTargetsVersion)` | `MintPlayer.AspNetCore.NodeServices.csproj` |
| Remove redundant `Microsoft.Extensions.DependencyInjection.Abstractions` (NU1510) | `Demo/Prerendering/Demo.Data/Demo.Data.csproj` |

Diff across M1–M4: **19 files, +63/−62** plus the two new docs. No `.cs` file is touched.

### M3 — CI ✅

`dotnet-version: 10.0.100` → `11.0.100-rc.1.26425.128` in `build-any.yml:26`, `build-master.yml:20`,
`pull-request.yml:20`. No other workflow change: there is no `setup-node` step, and
`dotnet pack --no-build` already works.

**Coverage double-counting — investigated and retired.** `dotnet test` now runs the suite twice, so
the upload glob `coverage/**/coverage.cobertura.xml` matches two reports instead of one. Measured:
the two are equivalent — same 53 source files, same `lines-covered="1301" lines-valid="2019"`, same
`line-rate="0.6443"`. Cobertura `filename` paths are TFM-independent, so a service that merges by
file takes the union and reports 64.43%, and one that sums doubles numerator *and* denominator and
still reports 64.43%. **The rate is invariant either way**, so no `-f net11.0` restriction is needed.
Only an absolute line *count* would differ, which this service does not gate on.

### M4 — Documentation ✅

No README in the repo states a TFM, and all badges are dynamic shields.io — **nothing to bump there.**
The stale build/Codacy badges pointing at `…SpaServices.Routing` are pre-existing and out of scope.

Historical `docs/` files naming `net10.0` are records of what shipped and stay as written. The one
real exception is a live compatibility claim:

- `docs/PRD-SPA-Build-Caching.md:137` — "NFR-2.1: The feature SHALL work with .NET 8.0, .NET 9.0, and
  .NET 10.0" → add .NET 11.0.

### M5 — Verification gate ✅

Full sweep, run once at the end, reproducing the CI sequence exactly:

| Step | Result |
|---|---|
| `dotnet restore` | exit 0 |
| `dotnet build -c Release --no-restore` | exit 0 |
| `dotnet test … --settings coverlet.runsettings --collect:"XPlat Code Coverage"` | exit 0 — **381 passed on net10.0, 381 passed on net11.0**, 0 failed, 0 skipped |
| `dotnet pack --no-build -c Release` | exit 0 — six nupkgs at `11.0.0-rc.1` |
| `ComputeSpaFolderHash` task invocation (Spike 2; no other step covers it) | exit 0, hash computed |

Coverage: 64.43% line, 55.36% branch, 1301/2019 lines across 53 files — unchanged from the
`net10.0` baseline, as expected for a bump that edits no `.cs` file.

## Risk register

| Risk | Severity | Status |
|---|---|---|
| Source generators fail to load under .NET 11 Roslyn | High | **Retired** — Spike 1 |
| FolderHasher task rename breaks SPA build caching silently | High | **Fixed + verified** — Spike 2 |
| Pack breaks under multi-targeting | Medium | **Fixed + verified** — Spike 4 |
| Coverage double-counted across two TFMs | Medium | **Retired** — rate is invariant, M3 |
| `rollForward: latestFeature` vs GA `11.0.100` | Low | **Open** — re-check Nov 2026 |
| `.hasherignore` auto-created in a consumer's SPA root on first build | Low | Pre-existing behaviour; noted, not changed |
