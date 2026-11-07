# Plan: Workspace modernization (ESM boot bundles first)

Implementation plan for [PRD-Workspace-Modernization.md](./PRD-Workspace-Modernization.md).
It supersedes `PLAN-Prerendering-ESM-Boot-Module.md`.

Branch: `feature/workspace-modernization`, from `master`. **One PR** (PRD D7). Spikes are
time-boxed and throwaway. Each one appends a short "Result" to this file, and that result drives the
milestones after it.

## Milestones

### M1 — Investigation ✅ Complete

Six read-only agents ran in two rounds.

| Agent | Scope | Outcome |
|---|---|---|
| **A** | `BootModulePath` → `prerenderer.js` → Node | Loading is **already ESM-capable** (#70). Found that `.mjs` is not watched. |
| **B** | Demo SSR build output | The SSR target is still the webpack `:server` builder (CJS). ESM has never been run. Every doc shows CJS. |
| **C** | External ESM / Angular research | Use `import()` + `pathToFileURL`, not `require(esm)`. The ESM cache means a reload needs a process restart. |
| **D** | Node/JS toolchain and C# hosting | The JS build never runs (publish only, broken yarn setup). Dependencies are TS 3 / Node 10 era. `prerenderer.js` has no source. `process.binding` is used. Newtonsoft is used under AOT. The temp-file/`NODE_PATH` setup is ignored by ESM. CI has no `setup-node`. There is an `es-modules` branch to reuse. |
| **E** | Demos and dev-loop helpers | Both default regexes match webpack console text. Found the `--watch`/`&&` issue, the demo dependency list to drop, the `sockjs-node` exclude, the Xsrf output-path casing bug, and the `npm run build:ssr:production` publish contract. |
| **F** | Spike S2 (static) — the `main.server.mjs` export surface on 19.2, 20.3 and 21.0 | The builder emits **no `renderApplication`**, so option A is dead. The `ɵgetOrCreateAngularServerApp` family is always present. `AngularServerApp` renders its own `index.server.html`, not ours. |

### M2 — PRD + plan ✅

This document and the PRD. **D1–D7 are settled** (2026-11-07). D1 was refined to the C1 one-liner
(`export { renderApplication } from '@angular/platform-server'`) after reading a fresh Angular 22.2
`ng new --ssr` scaffold at `C:\Repos\aa\my-app`, which is the reference app for the spikes.

### M3 — Spikes (as planned)

| Spike | Question | Time box | Exit |
|---|---|---|---|
| **S1** | Does today's loader render a real application-builder ESM bundle? Switch the demo's `server` target to `@angular/build:application`, keeping the `createServerRenderer` wrapper. Check that the chunks resolve, that the `isServerRenderer` flag survives esbuild's CJS→ESM interop, and the output layout. | ½ day | works / fails at a named line |
| **S2a** | C1, proven. Use the 22.2 reference app first, then 19.x, 20.x and 21.x. (1) Add the `renderApplication` re-export and a `prerenderProviders` export, then build. (2) `Object.keys(await import(main.server.mjs))` confirms both pass through `export *`. (3) A plain Node script calls `renderApplication(default, { document, url, platformProviders: prerenderProviders(data) })` and gets HTML that uses the injected data. (4) On 19.x, check whether `renderApplication` calls the bootstrap with a context. | 1 day | C1 works on 19–22 / names the failing major |
| **S2b** | Option B, demoted. Call `ɵgetOrCreateAngularServerApp().handle(new Request(url), data)` from a plain Node script. Check whether `html:transform:pre` can substitute `originalHtml`, and how it behaves under the default `RenderMode.Prerender`. | ½ day | adopt as a fallback / drop |
| **S7** | `security.allowedHosts`. Use the reference app's empty `allowedHosts`. Does `renderApplication` (C1) or `AngularServerApp` (B) reject or CSR-fallback a `url` whose host is the .NET request host? If so, decide how the host passes the url (for example a relative `url` plus an `APP_BASE_HREF`/origin provider), or document an `allowedHosts` requirement. | ½ day | url strategy |
| **S3** | Dev loop. Does `ng build --watch` print `Application bundle generation complete.` on **stdout or stderr**? Does a rebuild with `.mjs` watched restart Node cleanly? Hashed chunk names and delete-then-write output may need a debounce. What does `ng serve` (Vite) print? | ½ day | default regexes + debounce decision |
| **S4** | Node in tests. Can xUnit start the real entry script against `.cjs`/`.mjs` fixtures on CI? Decide the fixture layout (`Tests/NodeServices/Fixtures/**`, copied to the output folder) and the Node version gate. | ½ day | test pattern |
| **S5** | System.Text.Json migration. Inventory every Newtonsoft use, including the public surface (`Globals`, `SupplyData` payloads, user `customData` serialization). Run `dotnet publish -p:PublishAot=true` on a minimal consumer and capture the IL2026/IL3050 warnings before and after. | ½ day | list of API breaks for the release notes |
| **S6** | Script extraction without `%TEMP%`/`NODE_PATH`. Write the embedded `.cjs` to the app's base directory or `obj/`, or pack them as content files. Check that bare-specifier resolution still works for CJS boot modules that `require` from the app's `node_modules`. | ½ day | chosen location |

#### M3 results ✅ (2026-11-07)

Run against a copy of the 22.2 reference app (the user's workspace was not touched) and against the
migrated demo (Angular 21.2).

| Spike | Result |
|---|---|
| **S1** | Superseded by S2a. The application-builder bundle was loaded directly through `import()`, so the webpack `server` target and the `createServerRenderer` wrapper were not needed. |
| **S2a** | **Green on 22.2 and 21.2.** `main.server.mjs` exports `default, prerenderProviders, renderApplication, ɵdestroyAngularServerApp, ɵextractRoutesAndCreateRouteTree, ɵgetOrCreateAngularServerApp, ɵresetCompiledComponents`, so both additions pass through `export *`. `renderApplication(default, { document, url, platformProviders })` rendered the injected data: 37 ms on 22.2, and about 210 ms for the demo's `/person/1/ada-lovelace` with the message and the person from "OnSupplyData". 19.x and 20.x were not run. Their source has the same `renderApplication(bootstrap, options)` signature with a context, so they are expected to work, but this is unverified. |
| **S2b** | **Dropped.** `AngularServerApp.handle` builds its own document from `index.server.html`. Under the default `RenderMode.Prerender` it serves the build-time page, so it ignores `originalHtml` and "SupplyData". |
| **S7** | An absolute `url` is rejected with `NG05706` whatever its host, even localhost, when `allowedHosts` is empty. **A relative `url` works.** The host passes `requestPathAndQuery`. Also: an absolute `APP_BASE_HREF` (`origin + baseUrl`) throws `NG05703`, so `APP_BASE_HREF` must be relative too. |
| **S3** | `Application bundle generation complete. [...]` is printed on **stdout**, once per (re)build, so the occurrence is 1. Vite prints `➜  Local:   http://localhost:N/` wrapped in ANSI codes, which `EventedStreamReader` strips. **`ng serve` SSR-renders** when the build has `server` + `outputMode`. Setting `ssr: false` in a configuration does not stop it, and `"server": ""` is rejected. So the SSR options go in an `ssr` build configuration (see the PRD). No debounce was needed. |
| **S4** | xUnit can drive the real entry script: `NodeServicesFactory` over a temp fixture folder, with `WatchFileExtensions = []`. The fixtures are written by the test, under a path that contains a space. No fixture files are copied to the output folder. |
| **S5** | Newtonsoft was used in `HttpNodeInstance` (RPC), `RenderToStringResult.Globals` (`JObject`), and as a bare package reference in SpaServices. All three are replaced (`NodeRpcJson`, `JsonObject`). The API breaks are in the release notes. AOT: the reflection resolver is used, with suppressions on two methods. A publish of a consumer with AOT was not run. |
| **S6** | Kept the temp-file extraction (now `.cjs`, `internal sealed`) and `NODE_PATH`. See PRD revision D5. |

### M4 — W1: Embedded JS rewrite

- Replace the NodeServices `TypeScript/` folder, `webpack.config.js`, `package.json`, the lock file and
  `Yarn.MSBuild` with hand-written `entrypoint-http.cjs` and its utilities. Start from the
  `es-modules` branch's `HttpNodeInstanceEntryPoint.cjs`.
- Use plain `import(pathToFileURL(p).href)` and remove the `new Function` hack.
- Delete `PatchModuleResolutionLStat`. Keep the stdout/stderr overriding, argument parsing and
  exit-when-parent-exits.
- Rewrite `prerenderer.js` as `prerenderer.cjs` (~80 lines):
  - find the export
  - accept a flagged `createServerRenderer` function **or** a plain `(params) => Promise<result>`
    function (D1-C)
  - throw a clear error naming the export that was found
- Remove the legacy mode and `webpackConfig` (D2).
- Add a test that the embedded resources equal the `.cjs` sources byte for byte.

### M5 — W2: ESM boot contract and Angular path

- Export resolution in the loader, in this order:
  1. a flagged `createServerRenderer` function
  2. **`default` + `renderApplication` (C1)**: render the bootstrap with `originalHtml`, the `url` per
     S7, and `platformProviders` from the optional `prerenderProviders(data)` export
  3. a plain renderer function (C2)
  4. otherwise, a clear error that names what was found and shows the one-line fix
- If `default` is a bootstrap function but `renderApplication` is missing, the error must say
  exactly that and quote the line to add to `main.server.ts`.
- If S2b is green, add option B as the last fallback.
- Add `.mjs` and `.cjs` to `DefaultWatchFileExtensions`, plus a debounce if S3 needs one.
- Decide whether to publish an optional helper (`@mintplayer/ng-server-renderer`) for the C
  one-liner. It would ship in the same PR, as a source folder under `npm/`.

### M6 — W3: Dev-loop helpers

- `AngularPrerendererBuilder`:
  - default regex `Application bundle generation complete\.` with occurrence 1, accepting the old
    text too (D4)
  - read stderr if S3 says so
  - document a single `ng build --watch` script instead of `a && b`
- `AngularCliMiddleware`: default `Local:\s+(?<openbrowser>https?://\S+)`, falling back to the
  webpack text.

### M7 — W4: C# hosting

- Newtonsoft → System.Text.Json with a source-generated `JsonSerializerContext`, and
  `Globals: JsonObject?` (D3). Remove the Newtonsoft package references.
- Script extraction per S6, removing `StringAsTempFile` and `NODE_PATH` (D5).
- Small cleanups:
  - `Task.WaitAsync` in place of `OrThrowOnCancellation`
  - `Lazy<>` in `Prerenderer`
  - drop the finalizers
  - an options record for the `OutOfProcessNodeInstance` constructor
  - fix the nullable annotations and the inspector wording

### M8 — W5/W6: Demos and MSBuild

- Prerendering demo (PRD 5.1):
  - application builder with `server`
  - `@angular/build` targets and vitest
  - new `main.server.ts` (D1 shape) and `@angular/ssr` `provideServerRendering`
  - `BootModulePath` → `dist/server/main.server.mjs`
  - Vite `ExcludeUrls`
  - tsconfig alignment
  - drop the obsolete dependencies
- Xsrf demo: set `outputPath` explicitly and fix the path casing.
- Both demos move to `WebApplication.CreateBuilder`.
- `nodeservices.targets`:
  - keep `build:ssr:production` as the documented contract (or make it a property)
  - make the `node_modules` publish copy opt-out (`PublishNodeModules`, default **true** for
    compatibility, documented `false` for ESM bundles)

### M9 — W7: CI

- Add `actions/setup-node` with a Node 20/22/24 matrix on the test job.
- Bump the `checkout` and `setup-dotnet` actions.

### M10 — Docs and release notes

- Prerendering README: a "Boot module formats" section, recommending the Angular application
  builder. Include `angular.json`, `main.server.ts` and `BootModulePath` samples, the DI-data
  convention and a dev-loop section.
- Update every `dist/server/main.js` mention: the root, Abstractions and Routing READMEs.
- `RELEASE-NOTES.txt`: the major version, the breaking changes D2–D5 with a migration guide, and
  the new defaults.

### M11 — Tests (run once, at the end)

- Fixture integration tests per the PRD acceptance criteria.
- Unit tests for the new defaults: watch extensions and regexes.
- An STJ round-trip of `RenderToStringResult`.
- An AOT-publish check of a minimal consumer.
- Run the full suite once. The coverage gate (95/90) must hold.

## Implementation status (2026-11-07)

| Milestone | Status | Notes |
|---|---|---|
| M4 | ✅ | `entrypoint-http.cjs` and `prerenderer.cjs` are hand-written. The TypeScript, webpack, Yarn.MSBuild, package.json and lock file are gone. Legacy mode and `webpackConfig` are removed. Byte-equality test: `WorkspaceModernizationTests`. |
| M5 | ✅ | Resolution order: flagged → `default` + `renderApplication` → plain renderer → error. The missing-`renderApplication` error quotes the line. `.mjs` and `.cjs` are watched. No npm helper package: the one-liner needs none. |
| M6 | ✅ | `AngularPrerendererBuilder` defaults to the completion line, once (stdout). `AngularCliMiddleware` matches `Local:` and `open your browser on`. |
| M7 | ✅ | STJ with the reflection resolver (not source-generated; see the PRD G-AOT revision). `Globals` is a `JsonObject`. `StringAsTempFile` is internal. `Task.WaitAsync` replaces `OrThrowOnCancellation`, which is deleted. The finalizers are gone. `OutOfProcessNodeInstance` takes `NodeServicesOptions`. The nullable annotations and the inspector wording are fixed. `Prerenderer` keeps its lock instead of `Lazy<>` (see the PRD M7 revision). The Node process-lifecycle audit and its fixes are in the PRD, "Node process lifecycle". |
| M8 | ✅ | Prerendering demo migrated to `@angular/build`, `@angular/ssr`, an `ssr` configuration, `main.server.mjs`, `index.csr.html`, Vite `ExcludeUrls` and `PublishNodeModules=false`. The Xsrf demo's `outputPath` and casing are fixed. `PublishNodeModules` was added (default: `BuildServerSideRenderer`). Both demos use `WebApplication.CreateBuilder`, with an explicit `UseEndpoints` so controllers still run before the SPA middleware. The README snippets keep their `ConfigureServices`/`Configure` bodies, which apply to either hosting model. |
| M9 | ✅ | Node 22 in all three workflows. A `node-versions` job in `pull-request.yml` runs the real-Node tests on 20 and 24. The actions are `checkout@v7`, `setup-dotnet@v6` and `setup-node@v7`. |
| M10 | ✅ | Prerendering README ("The Angular side", "Boot module formats", workspace configuration, troubleshooting). The root, SpaServices, Abstractions and Routing READMEs are updated. RELEASE-NOTES for Prerendering, NodeServices and SpaServices. |
| M11 | see the PR | `PrerendererNodeIntegrationTests` (real Node: ESM with a chunk and TLA, flagged CJS, the Angular path, missing `renderApplication`, a bad plain renderer, camelCase keys, a path with a space) and `WorkspaceModernizationTests`. AOT check: `dotnet publish -p:PublishAot=true -r win-x64` of a minimal `CreateSlimBuilder` consumer (`AddNodeServices`, `UseSpaImproved`, `UseSpaPrerendering`) produced 0 IL warnings, after the `JsonSerializerOptions` initializer moved into a suppressed method. |
