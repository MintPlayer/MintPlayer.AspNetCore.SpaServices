# PRD: SPA Build Caching with Folder Hash Detection

## Overview

Implement intelligent build caching for Angular/React SPA projects that use the `MintPlayer.AspNetCore.NodeServices` package. The system will skip unnecessary `npm run build` or `npm run build:ssr` executions when the SPA source folder contents haven't changed, significantly reducing build times during development and CI/CD pipelines.

## Problem Statement

Currently, any project that has the `MintPlayer.AspNetCore.NodeServices` package installed (directly or transitively) will automatically trigger `npm run build:ssr` during publish operations, even when the contents of the SPA folder haven't changed. This causes:

1. **Wasted build time** - Unnecessary npm builds can add 30-120+ seconds to each build
2. **CI/CD inefficiency** - Pipeline builds repeatedly rebuild unchanged SPAs
3. **Developer frustration** - Local publish operations take longer than necessary
4. **Resource waste** - CPU and memory consumed by redundant builds

## Proposed Solution

Integrate the existing `MintPlayer.FolderHasher.Targets` package to compute a hash of the SPA folder contents before running npm build commands. The hash will be stored in a temporary file, and subsequent builds will compare the current hash against the stored hash to determine if a rebuild is necessary.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    MSBuild Build Pipeline                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  1. ComputeSpaFolderHash Target                                 │
│     ├── Read previous hash from $(IntermediateOutputPath)       │
│     ├── Compute current hash using ComputeFolderHashTask        │
│     ├── Compare hashes                                          │
│     └── Set $(SpaSourceChanged) property                        │
│                                                                  │
│  2. DebugEnsureNodeEnv Target (existing)                        │
│     └── Condition: '$(SpaSourceChanged)' == 'true' OR           │
│                    !Exists('$(SpaRoot)node_modules')            │
│                                                                  │
│  3. PublishRunWebpack Target (existing)                         │
│     └── Condition: '$(SpaSourceChanged)' == 'true' OR           │
│                    !Exists('$(SpaRoot)dist')                    │
│                                                                  │
│  4. SaveSpaFolderHash Target                                    │
│     └── Save current hash to $(IntermediateOutputPath)          │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Functional Requirements

### FR-1: Automatic Hash Computation

- **FR-1.1**: The system SHALL compute a SHA-256 hash of the SPA folder contents before build/publish operations
- **FR-1.2**: The hash computation SHALL respect `.hasherignore` files for excluding files/folders
- **FR-1.3**: The system SHALL provide sensible default ignore patterns for common SPA artifacts

### FR-2: Build Command Selection

The system SHALL select the appropriate npm build command based on the `BuildServerSideRenderer` property:

| BuildServerSideRenderer | Build Command | Description |
|-------------------------|---------------|-------------|
| `true` (default) | `npm run build:ssr` | Builds both client and server bundles for SSR/prerendering |
| `false` | `npm run build` | Builds client bundle only (no server-side rendering) |

For production/publish builds, the commands are:
- SSR enabled: `npm run build:ssr:production`
- SSR disabled: `npm run build -- --configuration production`

### FR-3: Build Skip Logic

- **FR-3.1**: The system SHALL skip `npm install` if the hash hasn't changed AND `node_modules` exists
- **FR-3.2**: The system SHALL skip the selected build command if the hash hasn't changed AND the output folder exists
- **FR-3.3**: The system SHALL always rebuild if the hash file doesn't exist (first build)
- **FR-3.4**: The system SHALL always rebuild if explicitly requested via MSBuild property

### FR-4: Hash Storage

- **FR-4.1**: The hash SHALL be stored in `$(IntermediateOutputPath)spa-folder.hash`
- **FR-4.2**: The hash file SHALL contain only the hash string (no metadata)
- **FR-4.3**: The hash file SHALL be excluded from source control (via .gitignore patterns for obj/)

### FR-5: Default Ignore Patterns

The system SHALL provide the following default ignore patterns (can be overridden via `.hasherignore`):

```
# Build outputs
dist/
dist-server/
.angular/

# Dependencies (handled separately)
node_modules/

# IDE and editor files
.idea/
.vscode/
*.swp
*.swo
*~

# OS files
.DS_Store
Thumbs.db

# Test outputs
coverage/
test-results/

# Cache directories
.cache/
.npm/
```

### FR-6: Configuration Options

| MSBuild Property | Default | Description |
|------------------|---------|-------------|
| `EnableSpaBuildCaching` | `true` | Enable/disable the caching feature |
| `SpaHashFilePath` | `$(IntermediateOutputPath)spa-folder.hash` | Location to store the hash file |
| `ForceSpaBuild` | `false` | Force rebuild even if hash unchanged |
| `SpaHashIgnorePatterns` | (see FR-4) | Additional patterns to ignore |

### FR-7: Transitive Package Support

- **FR-7.1**: The caching feature SHALL work when the package is installed transitively
- **FR-7.2**: All targets and tasks SHALL be available via `buildTransitive` package folder

## Non-Functional Requirements

### NFR-1: Performance

- **NFR-1.1**: Hash computation for a typical SPA folder (excluding node_modules) SHALL complete in < 2 seconds
- **NFR-1.2**: The caching mechanism SHALL reduce redundant build times by 90%+ when no changes detected

### NFR-2: Compatibility

- **NFR-2.1**: The feature SHALL work with .NET 8.0, .NET 9.0, and .NET 10.0
- **NFR-2.2**: The feature SHALL work with Angular, React, Vue, and other npm-based SPAs
- **NFR-2.3**: The feature SHALL not break existing projects that don't use caching

### NFR-3: Reliability

- **NFR-3.1**: Hash collisions SHALL be statistically negligible (SHA-256)
- **NFR-3.2**: Failed hash computation SHALL trigger a rebuild (fail-safe)
- **NFR-3.3**: Corrupted hash files SHALL trigger a rebuild

## Technical Implementation

### Package Dependencies

Add a package reference to `MintPlayer.FolderHasher.Targets` in `MintPlayer.AspNetCore.NodeServices.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="MintPlayer.FolderHasher.Targets" Version="10.1.0" PrivateAssets="all" />
</ItemGroup>
```

The FolderHasher.Targets DLLs will need to be bundled into the NodeServices package's `buildTransitive` folder.

### Modified nodeservices.props

```xml
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <EnableSpaBuilder Condition=" '$(EnableSpaBuilder)' == '' ">true</EnableSpaBuilder>
    <SpaRoot Condition=" '$(SpaRoot)' == '' ">ClientApp\</SpaRoot>
    <BuildServerSideRenderer Condition=" '$(BuildServerSideRenderer)' == '' ">true</BuildServerSideRenderer>

    <!-- New caching properties -->
    <EnableSpaBuildCaching Condition=" '$(EnableSpaBuildCaching)' == '' ">true</EnableSpaBuildCaching>
    <SpaHashFilePath Condition=" '$(SpaHashFilePath)' == '' ">$(IntermediateOutputPath)spa-folder.hash</SpaHashFilePath>
    <ForceSpaBuild Condition=" '$(ForceSpaBuild)' == '' ">false</ForceSpaBuild>
  </PropertyGroup>

  <ItemGroup Condition=" '$(EnableSpaBuilder)' == 'true' ">
    <!-- Existing exclusions... -->
  </ItemGroup>
</Project>
```

### Modified nodeservices.targets

```xml
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">

  <!-- Import the FolderHasher task -->
  <UsingTask TaskName="MintPlayer.FolderHasher.MSBuild.ComputeFolderHashTask"
             AssemblyFile="$(MSBuildThisFileDirectory)MintPlayer.FolderHasher.MSBuild.dll" />

  <!-- Compute SPA folder hash and determine if rebuild is needed -->
  <Target Name="ComputeSpaFolderHash"
          BeforeTargets="DebugEnsureNodeEnv;PublishRunWebpack"
          Condition=" '$(EnableSpaBuilder)' == 'true' And '$(EnableSpaBuildCaching)' == 'true' ">

    <!-- Read previous hash if it exists -->
    <PropertyGroup>
      <PreviousSpaHash Condition="Exists('$(SpaHashFilePath)')">$([System.IO.File]::ReadAllText('$(SpaHashFilePath)').Trim())</PreviousSpaHash>
      <PreviousSpaHash Condition="!Exists('$(SpaHashFilePath)')"></PreviousSpaHash>
    </PropertyGroup>

    <!-- Compute current hash -->
    <ComputeFolderHashTask FolderPath="$(SpaRoot)" Condition="Exists('$(SpaRoot)')">
      <Output TaskParameter="Hash" PropertyName="CurrentSpaHash" />
    </ComputeFolderHashTask>

    <!-- Determine if source changed -->
    <PropertyGroup>
      <SpaSourceChanged Condition=" '$(ForceSpaBuild)' == 'true' ">true</SpaSourceChanged>
      <SpaSourceChanged Condition=" '$(ForceSpaBuild)' != 'true' And '$(PreviousSpaHash)' == '' ">true</SpaSourceChanged>
      <SpaSourceChanged Condition=" '$(ForceSpaBuild)' != 'true' And '$(PreviousSpaHash)' != '' And '$(PreviousSpaHash)' != '$(CurrentSpaHash)' ">true</SpaSourceChanged>
      <SpaSourceChanged Condition=" '$(ForceSpaBuild)' != 'true' And '$(PreviousSpaHash)' != '' And '$(PreviousSpaHash)' == '$(CurrentSpaHash)' ">false</SpaSourceChanged>
    </PropertyGroup>

    <Message Importance="high"
             Text="SPA source unchanged (hash: $(CurrentSpaHash)). Skipping npm build."
             Condition=" '$(SpaSourceChanged)' == 'false' " />
    <Message Importance="high"
             Text="SPA source changed. Previous: $(PreviousSpaHash), Current: $(CurrentSpaHash)"
             Condition=" '$(SpaSourceChanged)' == 'true' And '$(PreviousSpaHash)' != '' " />
  </Target>

  <!-- Debug: Ensure Node environment -->
  <Target Name="DebugEnsureNodeEnv"
          BeforeTargets="Build"
          DependsOnTargets="ComputeSpaFolderHash"
          Condition=" '$(EnableSpaBuilder)' == 'true' And '$(Configuration)' == 'Debug' And !Exists('$(SpaRoot)node_modules') ">
    <!-- Existing implementation... -->
  </Target>

  <!-- Publish: Build the SPA -->
  <Target Name="PublishRunWebpack"
          AfterTargets="ComputeFilesToPublish"
          DependsOnTargets="ComputeSpaFolderHash"
          Condition=" '$(EnableSpaBuilder)' == 'true' And ('$(SpaSourceChanged)' == 'true' Or '$(EnableSpaBuildCaching)' != 'true' Or !Exists('$(SpaRoot)dist')) ">

    <!-- Install dependencies -->
    <Exec WorkingDirectory="$(SpaRoot)" Command="npm install" />

    <!-- Build command selection based on BuildServerSideRenderer -->
    <!-- If BuildServerSideRenderer == true  => npm run build:ssr:production -->
    <!-- If BuildServerSideRenderer == false => npm run build -- --configuration production -->
    <Exec WorkingDirectory="$(SpaRoot)"
          Command="npm run build -- --configuration production"
          Condition=" '$(BuildServerSideRenderer)' != 'true' " />
    <Exec WorkingDirectory="$(SpaRoot)"
          Command="npm run build:ssr:production"
          Condition=" '$(BuildServerSideRenderer)' == 'true' " />

    <!-- Include the newly-built files in the publish output -->
    <ItemGroup>
      <DistFiles Include="$(SpaRoot)dist\**; $(SpaRoot)dist-server\**" />
      <DistFiles Include="$(SpaRoot)node_modules\**" Condition="'$(BuildServerSideRenderer)' == 'true'" />
      <ResolvedFileToPublish Include="@(DistFiles->'%(FullPath)')" Exclude="@(ResolvedFileToPublish)">
        <RelativePath>%(DistFiles.Identity)</RelativePath>
        <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
        <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
      </ResolvedFileToPublish>
    </ItemGroup>

    <!-- Save hash after successful build -->
    <WriteLinesToFile File="$(SpaHashFilePath)"
                      Lines="$(CurrentSpaHash)"
                      Overwrite="true"
                      Condition=" '$(EnableSpaBuildCaching)' == 'true' " />
  </Target>

  <!-- Fallback: Save hash even when build is skipped (for clean scenarios) -->
  <Target Name="SaveSpaFolderHash"
          AfterTargets="PublishRunWebpack"
          Condition=" '$(EnableSpaBuilder)' == 'true' And '$(EnableSpaBuildCaching)' == 'true' And '$(SpaSourceChanged)' == 'false' ">
    <WriteLinesToFile File="$(SpaHashFilePath)"
                      Lines="$(CurrentSpaHash)"
                      Overwrite="true" />
  </Target>

</Project>
```

### Default .hasherignore Template

Projects can create a `.hasherignore` file in their `ClientApp` folder. If not present, the FolderHasher will use its default behavior. We should document recommended patterns:

```
# Recommended .hasherignore for Angular/React SPAs

# Build outputs - these are results, not inputs
dist/
dist-server/
.angular/
build/
out/

# Dependencies - changes to package.json will trigger rebuild
node_modules/

# IDE files
.idea/
.vscode/
*.swp
*~

# Test artifacts
coverage/
.nyc_output/

# Caches
.cache/
.eslintcache
.stylelintcache
```

## Migration Path

### Existing Projects

1. **No action required** - Caching is enabled by default
2. **Opt-out available** - Set `<EnableSpaBuildCaching>false</EnableSpaBuildCaching>` to disable
3. **First build** - Will run normally and create the hash file
4. **Subsequent builds** - Will use caching automatically

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| Clean build (`dotnet clean`) | Hash file deleted, next build runs fully |
| Package update (package.json changed) | Hash changes, triggers rebuild |
| Only .ts/.tsx files changed | Hash changes, triggers rebuild |
| Only README.md changed | Hash changes (could add to .hasherignore if undesired) |
| node_modules deleted manually | `!Exists('$(SpaRoot)node_modules')` condition triggers npm install |
| dist folder deleted manually | `!Exists('$(SpaRoot)dist')` condition triggers build |

## Testing Plan

### Unit Tests

1. Hash computation with various folder structures
2. Hash comparison logic
3. Default ignore pattern matching

### Integration Tests

1. First build creates hash file
2. Unchanged source skips build
3. Changed source triggers build
4. ForceSpaBuild=true triggers build
5. Deleted hash file triggers build
6. Deleted dist folder triggers build
7. Works with transitive package reference

### Performance Tests

1. Measure hash computation time for various SPA sizes
2. Compare total build time with/without caching
3. Memory usage during hash computation

## Success Metrics

| Metric | Target |
|--------|--------|
| Build time reduction (no changes) | > 90% |
| Hash computation overhead | < 2 seconds |
| Zero regressions | 100% |
| Adoption rate | Default enabled |

## Timeline

| Phase | Description | Duration |
|-------|-------------|----------|
| 1 | Add FolderHasher dependency and bundle DLLs | 1 |
| 2 | Implement hash computation target | 1 |
| 3 | Modify existing targets with caching conditions | 1 |
| 4 | Testing and edge case handling | 1 |
| 5 | Documentation updates | 1 |

## Open Questions

1. **Q**: Should we include a `--force` CLI parameter for `dotnet publish`?
   **A**: Not in initial version. `ForceSpaBuild` property is sufficient.

2. **Q**: Should we hash `package-lock.json` / `yarn.lock` separately to detect dependency changes?
   **A**: No, these files are already included in the folder hash automatically.

3. **Q**: Should the hash file location be configurable per-project?
   **A**: Yes, via `SpaHashFilePath` property.

4. **Q**: How to handle multi-SPA projects (multiple ClientApp folders)?
   **A**: Each SPA should have its own `SpaRoot` setting and will get its own hash file. Future enhancement could support multiple SPAs explicitly.

## Appendix A: FolderHasher.Targets Integration

The `MintPlayer.FolderHasher.Targets` package provides:

- `ComputeFolderHashTask` - MSBuild task that computes SHA-256 hash of folder contents
- `.hasherignore` support - gitignore-style pattern matching for exclusions
- Streaming for large files - Memory-efficient handling of large files
- Access error handling - Gracefully skips inaccessible files

The task is already production-ready and available on NuGet.

## Appendix B: Example Build Output

### Scenario 1: No changes detected (build skipped)

```
Build started...
1>------ Publish started: Project: Demo.Web, Configuration: Release ------
1>ComputeSpaFolderHash:
1>  Computing folder hash for 'ClientApp\'...
1>  Computed folder hash: a1b2c3d4e5f6...
1>  SPA source unchanged (hash: a1b2c3d4e5f6...). Skipping npm build.
1>PublishRunWebpack:
1>  Skipped (SpaSourceChanged=false)
1>Demo.Web -> C:\Repos\Demo\bin\Release\net10.0\publish\
========== Publish: 1 succeeded ==========
```

### Scenario 2: Changes detected with SSR enabled (BuildServerSideRenderer=true)

```
Build started...
1>------ Publish started: Project: Demo.Web, Configuration: Release ------
1>ComputeSpaFolderHash:
1>  Computing folder hash for 'ClientApp\'...
1>  Computed folder hash: x7y8z9...
1>  SPA source changed. Previous: a1b2c3d4e5f6..., Current: x7y8z9...
1>PublishRunWebpack:
1>  npm install
1>  npm run build:ssr:production
1>  Saved hash: x7y8z9...
1>Demo.Web -> C:\Repos\Demo\bin\Release\net10.0\publish\
========== Publish: 1 succeeded ==========
```

### Scenario 3: Changes detected with SSR disabled (BuildServerSideRenderer=false)

```
Build started...
1>------ Publish started: Project: Demo.Web, Configuration: Release ------
1>ComputeSpaFolderHash:
1>  Computing folder hash for 'ClientApp\'...
1>  Computed folder hash: x7y8z9...
1>  SPA source changed. Previous: a1b2c3d4e5f6..., Current: x7y8z9...
1>PublishRunWebpack:
1>  npm install
1>  npm run build -- --configuration production
1>  Saved hash: x7y8z9...
1>Demo.Web -> C:\Repos\Demo\bin\Release\net10.0\publish\
========== Publish: 1 succeeded ==========
```
