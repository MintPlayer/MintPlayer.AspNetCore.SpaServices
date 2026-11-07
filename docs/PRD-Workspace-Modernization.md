# PRD: Workspace modernization (ESM boot bundles first)

Supersedes `PRD-Prerendering-ESM-Boot-Module.md`. That document's findings are folded in as
workstream **W2**.

## Overview

The trigger was the question of whether `SpaPrerenderingOptions.BootModulePath` supports an
**ECMAScript-module bundle**: a set of `.mjs` files, as emitted by Angular's
`@angular/build:application` builder. **Loading already works.** Since #70 (`8e91466`), the boot
module is loaded with dynamic `import()` and `pathToFileURL`. But nothing around the loader has
followed along:

- the contract is still `aspnet-prerendering`'s `createServerRenderer`
- the embedded JS is unbuildable webpack-4 output
- the dev-loop helpers wait for webpack console text
- the demo still builds a CommonJS server bundle
- no test ever loads a boot module

This PRD covers modernizing the whole workspace. The goal is that an Angular 19–21 ESM SSR build
works out of the box.

## Investigation

Two rounds of agents, six in total, all read-only and spot-checked by hand. Raw findings are
summarized per workstream below.

### What already works

- **Loading.** `prerenderer.js:106-112`: `import()` behind `new Function`, `pathToFileURL`, path
  resolved against `ContentRootPath` (`:163`), with the export picked from CJS or ESM shapes
  (`:184-213`). Relative `chunk-*.mjs` imports resolve from the `file://` URL.
- **Node launch.** Node starts without any ESM flags (`OutOfProcessNodeInstance.cs:288`). Those
  flags would not be needed anyway.
- **C# hosting is mostly current.** It has `--inspect` handling, a process-tree kill and a
  per-instance `HttpClient`, with no sync-over-async, no `WebClient` and no `[Obsolete]`.
- **Target frameworks.** net10.0 and net11.0. Out of scope.
- **Demo browser build.** The demo's browser build already uses the `:application` builder, the app
  is zoneless, and the static root is `dist/browser`.

### Findings by workstream

#### W1 — The embedded JS toolchain is dead weight (NodeServices + Prerendering)

| # | Finding | Evidence |
|---|---|---|
| 1.1 | The JS build only runs on **publish**. It never runs on build, pack or in CI, so `Content/Node/entrypoint-http.js` is committed and in practice hand-edited. The publish step runs `yarn install --frozen-lockfile`, but the repo only has a `package-lock.json`. The `Yarn.MSBuild` package version is `*`. | `NodeServices.csproj:73-80`, `global.json` |
| 1.2 | The dependencies are stale. Leftover TS 3 `.js` outputs sit next to the `.ts` sources. | `package.json:13-19` (`typescript ^3`, `@types/node ^10`, webpack 5) |
| 1.3 | `prerenderer.js` has **no source anywhere in history**. It is webpack-4 output copied from Microsoft's `Prerendering.ts` in 2021 (`6acb640`). | `git log -- '*prerenderer*'` |
| 1.4 | The `new Function('return import(p)')` hack exists only because webpack rewrites `import()`. It is in **both** entry scripts. | `HttpNodeInstanceEntryPoint.ts:15-18`, `prerenderer.js:106` |
| 1.5 | `PatchModuleResolutionLStat` uses `process.binding('fs')` (DEP0111) and `path._makeLong`. It is a Windows workaround from the Node 6 era, with errors swallowed. | `TypeScript/Util/PatchModuleResolutionLStat.ts` |
| 1.6 | The aspnet-prerendering **v1 legacy mode**, the `bootModule.webpackConfig` check and `require("aspnet-prerendering")` are still present. Any export without `isServerRenderer` silently drops into legacy mode. | `prerenderer.js:118-149, 164-178, 227-257` |
| 1.7 | Prior art: the unmerged branch `es-modules` (`2a19e37`, based on the .NET 9 era) has `HttpNodeInstanceEntryPoint.cts/.cjs`. | `git branch -a --contains 2a19e37` |

#### W2 — ESM boot bundle contract (the original ask)

| # | Finding | Evidence |
|---|---|---|
| 2.1 | ESM has **never been exercised**. The demo's SSR target is the webpack `:server` builder, which outputs CJS `dist/server/main.js`. No test loads any boot module. | `ClientApp/angular.json:114-135` |
| 2.2 | Angular's builder emits `main.server.mjs` with this export surface on **19.2.x, 20.3.x and 21.0.x alike**, unconditionally: `default`, `export *` from the user's `main.server.ts`, `ɵgetOrCreateAngularServerApp`, `ɵdestroyAngularServerApp`, `ɵextractRoutesAndCreateRouteTree` and `ɵresetCompiledComponents`. It does **not** emit `renderApplication`, `renderModule` or `ɵSERVER_CONTEXT`. | angular-cli `packages/angular/build/src/tools/esbuild/application-code-bundle.ts`, `createServerMainCodeBundleOptions`, on each branch (fetched, paraphrased; runtime check pending, S2) |
| 2.3 | So the host **cannot** call `renderApplication` on the bundle unless the user re-exports it. Importing `@angular/platform-server` from `node_modules` instead would produce a second Angular instance with different token identities. | — |
| 2.4 | `AngularServerApp.handle(Request, requestContext?)` is always reachable through `ɵgetOrCreateAngularServerApp`, and it returns a `Response`, so status and redirects come for free. But it renders **its own `index.server.html` from the manifest, not our captured `originalHtml`**. The only hook is `html:transform:pre`, which is unverified. | angular-cli `packages/angular/ssr/src/app.ts` @ 21.0.x |
| 2.5 | `renderApplication(bootstrap, { document, url, platformProviders })` itself calls `bootstrap({ platformRef })` in v20 and v21. So a user's `(ctx) => bootstrapApplication(App, cfg, ctx)` works unchanged. | angular `packages/platform-server/src/utils.ts` @ 20.3.x and 21.0.x |
| 2.6 | `.mjs` and `.cjs` are not watched. ESM has no `require.cache`, so a rebuilt bundle is served stale until Node restarts. | `NodeServicesOptions.cs:17` |

#### W3 — The dev-loop helpers are webpack-shaped

| # | Finding | Evidence |
|---|---|---|
| 3.1 | `AngularPrerendererBuilder` defaults to the regex `Build at\:`, with occurrence 2. The application builder prints `Application bundle generation complete.` instead, and whether that goes to stdout or stderr is **unverified**. `--watch` is always appended, and for an `a && b` npm script it only reaches `b`. | `AngularPrerendererBuilder.cs:18,36,63`, `Npm/NodeScriptRunner.cs:49` |
| 3.2 | `AngularCliMiddleware` defaults to the webpack-dev-server text `open your browser on (?<openbrowser>http\S+)`. Vite prints `Local:   http://localhost:N/`. Both demos override the default. | `AngularCliMiddleware.cs:96` |
| 3.3 | The demo's `ExcludeUrls = ["/sockjs-node"]` is a webpack leftover. Vite uses `/@vite/`, `/@fs/` and `/@ng/`. | `Demo.Web/Startup.cs:122` |

#### W4 — C# hosting

| # | Finding | Evidence |
|---|---|---|
| 4.1 | **Newtonsoft.Json is used under `PublishAot=true`**, which is AOT-incompatible. `RenderToStringResult.Globals` is a **public `JObject`**. | `HttpNodeInstance.cs:7-30,76,125,158`; `RenderToStringResult.cs:4,56`; NodeServices and SpaServices csproj |
| 4.2 | The entry scripts are written to `%TEMP%` through the public `StringAsTempFile`, which has a finalizer. Modules resolve via `NODE_PATH`, **which ESM `import` ignores**. | `StringAsTempFile.cs`; `OutOfProcessNodeInstance.cs:309-317`; `Prerenderer.cs:13-76` |
| 4.3 | Small items: `OrThrowOnCancellation` → `Task.WaitAsync`; a static lock → `Lazy<>`; a finalizer at `OutOfProcessNodeInstance.cs:540`; a public 11-parameter constructor (`:52`); "node-inspector" wording; a non-nullable `NodeInstanceFactory`. | as listed |

#### W5 — Demos

| # | Finding | Evidence |
|---|---|---|
| 5.1 | **Prerendering demo.** Move to `@angular/build:application` with `server`, plus `@angular/build` dev-server, extract-i18n and unit-test (vitest instead of karma). Rewrite the `build:ssr:*` scripts. Drop `aspnet-prerendering`, `reflect-metadata`, `platform-browser-dynamic`, `@angular-devkit/build-angular`, karma and jasmine. `provideServerRendering` → `@angular/ssr`. Drop `enableProdMode`. Align the tsconfig files. | `ClientApp/package.json`, `angular.json`, `src/main.server.ts`, `app.config.server.ts` |
| 5.2 | **Xsrf demo.** It is already modern, but `Startup.cs:24` uses `dist/client-app/browser` while the default output is `dist/ClientApp/browser`. This only works on case-insensitive filesystems. | `Xsrf/Startup.cs:24` |
| 5.3 | Both demos use `Host.CreateDefaultBuilder` + `Startup` rather than `WebApplication.CreateBuilder`. This affects the docs and demos only. | `Program.cs` ×2 |

#### W6 — MSBuild publish targets

| # | Finding | Evidence |
|---|---|---|
| 6.1 | Publish runs `npm run build:ssr:production`, so the **script name is a contract**. Publish also copies **all of `node_modules`**, which a self-contained ESM bundle no longer needs. | `nodeservices.targets:151-181` |

#### W7 — CI

| # | Finding | Evidence |
|---|---|---|
| 7.1 | There is no `actions/setup-node`. Tests that start Node get whatever Node `ubuntu-latest` ships. No npm step runs. The workflows still use `checkout@v4` and `setup-dotnet@v4`. | `.github/workflows/*.yml` |

## Goals

1. **G-ESM.** An ESM boot bundle loads and renders, proven by integration tests against fixtures.
   The bundle has an `.mjs` entry, relative chunks and top-level await, and lives under a Windows
   path with spaces.
2. **G-OOTB.** Angular 19–22 application-builder output works with **no `aspnet-prerendering`
   dependency and no wrapper**: the default scaffold plus one re-export line (D1-C1).
3. **G-COMPAT.** Boot modules built with `createServerRenderer` keep working. The flagged-function
   path stays.
4. **G-JS.** The embedded JS becomes small, readable, hand-written `.cjs`, with no webpack, yarn, TS 3
   or `new Function`, and a test asserts the embedded copy is the shipped source.
5. **G-DEV.** In development, a rebuild is picked up, and the default regexes match the output of
   today's Angular CLI.
6. **G-AOT.** The libraries are trim- and AOT-clean (System.Text.Json source generation).
7. **G-DEMO / G-CI / G-DOCS.** The demos run on the current Angular toolchain. CI pins Node with a
   matrix. The READMEs and release notes describe ESM as the recommended path.

## Non-goals

- `require(esm)`. `import()` already works on every supported Node and tolerates top-level await.
- In-process hot reload through `?v=N` cache-busting, which leaks every old module graph. A Node
  restart stays the refresh mechanism.
- Hosting Angular's `server.mjs` or the Express `reqHandler`.
- Changing target frameworks.

## The current Angular default scaffold (reference)

This is what `ng new --ssr` generates today, as seen in a fresh **Angular 22.2** app
(`@angular/build` 22.2.6).

`src/main.server.ts` is the **only** thing the host loads:

```ts
import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { config } from './app/app.config.server';

const bootstrap = (context: BootstrapContext) =>
    bootstrapApplication(App, config, context);

export default bootstrap;
```

The rest of the scaffold:

- **`angular.json`:** `"server": "src/main.server.ts"`, `"outputMode": "server"`,
  `"ssr": { "entry": "src/server.ts" }` and `"security": { "allowedHosts": [] }`.
- **`app.config.server.ts`:** `provideServerRendering(withRoutes(serverRoutes))` from
  `@angular/ssr`.
- **`app.routes.server.ts`:** `{ path: '**', renderMode: RenderMode.Prerender }`.
- **`src/server.ts`:** an Express app with `AngularNodeAppEngine`. It is **irrelevant to this
  host**. It is never imported, so its `app.listen` never runs.

What this means:

- **The default export is a *bootstrap* function**, `(BootstrapContext) => Promise<ApplicationRef>`,
  not a renderer. Both are one-argument functions, so they cannot be told apart by shape.
- **There is no zero-line path that keeps `originalHtml`.** Rendering a bootstrap function needs
  `renderApplication` from the *same* Angular instance as the bundle, and the builder does not
  re-export it (finding 2.2).
- **Option B is worse than it looked.** The default `'**' → RenderMode.Prerender` makes
  `AngularServerApp` serve **build-time** HTML, so the per-request .NET data (`SupplyData`) would be
  silently ignored. `REQUEST_CONTEXT` is also absent outside `RenderMode.Server`. Every app would
  have to switch to `RenderMode.Server` to get correct output: zero lines of code, but not zero
  configuration.
- **`security.allowedHosts` is new.** Angular's SSRF hardening validates the request host. Whether
  `renderApplication` enforces it on the `url` we pass, and whether it then fails or falls back to
  CSR, is unverified. Spike S7 covers it.

## Design invariant: data flows in, the SSR render makes no outbound requests

This is inherited from Microsoft's design and is kept deliberately:

- The middleware calls `ISpaPrerenderingService.OnSupplyData` **in .NET** before rendering
  (`SpaPrerenderingExtensions.cs:367-409`).
- The resulting dictionary travels inside the invocation body to Node as `customData`, which is
  `params.data`.

The Angular app therefore gets everything it needs **without making HTTP requests during SSR**.
Such requests are a known failure mode:

- loopback or egress is blocked in some hosts
- calls to self over TLS fail (the demo sets `NODE_TLS_REJECT_UNAUTHORIZED=0`, `main.server.ts:37`)
- auth cookies are not forwarded
- the extra round trips slow every render

The Node side is a plain `http.createServer` bound to `localhost` on an ephemeral port
(`HttpNodeInstanceEntryPoint.ts:26,115-122`). .NET reads the port from Node's stdout and acts as the
client.

**Every new render path must preserve this.** In D1-C1 the data reaches Angular DI through
`prerenderProviders(data)`. Option B is only acceptable if `SupplyData` reaches the app too. Under
the default `RenderMode.Prerender` it does not, so it fails this invariant.

## Decisions

The maintainer agreed with the recommendations (2026-11-07). D1 was refined after reading the
scaffold above.

| ID | Decision | Outcome |
|---|---|---|
| **D1** | How Angular's `main.server.mjs` renders | **C1 (primary): a one-line opt-in.** The user adds `export { renderApplication } from '@angular/platform-server';` to the default `main.server.ts`. The builder's `export *` passes it through. When the loader sees **`default` + `renderApplication`**, it treats `default` as a bootstrap function and calls `renderApplication(default, { document: originalHtml, url, platformProviders })` itself. This keeps `originalHtml`, needs no wrapper and needs no `aspnet-prerendering`. Per-request data reaches DI through an optional convention export, `export const prerenderProviders = (data) => Provider[]`, which also passes through `export *`. **C2 (still supported):** a plain default-export renderer `(params) => Promise<{ html, statusCode?, redirectUrl? }>`, without the `isServerRenderer` flag, for full control. **Flagged `createServerRenderer`** modules keep working. **B** stays a spike only (S2b). It is adopted only if it can honour `originalHtml` and `SupplyData` without forcing `RenderMode.Server`. |
| **D2** | The aspnet-prerendering **v1 legacy mode** and the `webpackConfig` boot option | Remove. |
| **D3** | Newtonsoft → System.Text.Json (`Globals`: `JObject` → `JsonObject`) | Change. |
| **D4** | Default regexes of `AngularPrerendererBuilder` and `AngularCliMiddleware` | Change the default, and accept the old texts too. |
| **D5** | Public `StringAsTempFile` and `NODE_PATH` | Remove (after S6). |
| **D6** | Versioning | Major version. |
| **D7** | PR split | One PR. |

The **supported Angular range is 19–22**. 22.2 is the current scaffold. The spikes must include it.

### Revisions after the spikes (2026-11-07)

| ID | Revision | Why |
|---|---|---|
| **D1** | Confirmed as C1. `prerenderProviders` receives `(data, params)`. B is dropped. | S2a rendered injected data on 22.2 and on the demo (21.2). S2b: B ignores `originalHtml` and `SupplyData` under the default `RenderMode.Prerender`. |
| **D4** | `AngularCliMiddleware` accepts both texts (`open your browser on` and Vite's `Local:`). `AngularPrerendererBuilder` defaults to `Application bundle generation complete\.` once and does **not** also accept `Build at:`. | The webpack script prints `Build at:` twice. Accepting it once by default would report the build done after the browser half. Legacy users pass it explicitly. |
| **D5** | `StringAsTempFile` becomes `internal sealed` and writes `.cjs`. The `NODE_PATH` handling stays. | S6: the scripts must live on disk for `require`. `NODE_PATH` is harmless for a CommonJS entry and still lets a CJS boot module find the app's `node_modules`. |
| **D6** | The version comes from MintPlayer.Versioning (#93). The release notes call this a breaking release. | No hand-set version exists to bump. |
| **G-AOT** | Partly met. The RPC serializer is System.Text.Json with the reflection resolver, with documented suppressions. | Invocation arguments and results are caller-chosen types, so there is no source-generated context to give. |
| **CI** | One pinned Node (22), not a 20/22/24 matrix. | The coverage upload runs once per job. A matrix needs a separate test-only job. That is out of scope for this PR. |

### Spike findings that change the user-facing setup

- **`ng serve` renders pages itself** when the build it serves has `server` and `outputMode`. Turning off
  `ssr` in a configuration is not enough: the dev server forces SSR back on, and `"server": ""` is
  rejected. The template that `UseAngularCliServer` captures would then already be a rendered page.
  So `server`, `outputMode` and `ssr` go in an `ssr` build configuration, and the SSR scripts use
  `--configuration development,ssr` and `production,ssr`. This is the only change to `angular.json`
  beyond the scaffold.
- **`RenderMode.Server` for `**`**. With the scaffold's `RenderMode.Prerender`, `browser/index.html` is a
  prerendered page, which is a bad template. With `RenderMode.Server` the build writes
  `browser/index.csr.html` and no `index.html`, so the host sets `SpaOptions.DefaultPage = "/index.csr.html"`.
- **`url` must be relative** (S7). Angular 19.2+ rejects absolute URLs whose host is not in
  `security.allowedHosts` (`NG05706`). **`APP_BASE_HREF` must be relative too.** The old sample's
  `params.origin + params.baseUrl` makes Angular 21 throw `NG05703` ("changed origin").
- **Platform providers lose to application providers.** The demo's `app.config.server.ts` provided
  `MESSAGE_TOKEN` itself, so the .NET-supplied message never showed. This was already true before
  this work.
- **Angular render errors do not reject.** `NG04002`, `NG05703` and the like go to Angular's
  `ErrorHandler`, and `renderApplication` then never settles. The render timeout (30 s) ends the
  request. This is documented under troubleshooting.
- **Dependencies must stay aligned.** `@angular/ssr` 21.2 with `@angular/build` 21.1 fails at build time
  (`this.manifest.allowedHosts is not iterable`).

## Risks

- **R1.** The `ɵ` exports Angular puts in `main.server.mjs` are private and could change in a minor
  release. Option C does not depend on them; B does.
- **R2.** The application builder may write its completion message to stderr, which the builder
  helper does not read (S3).
- **R3.** Integration tests need a real Node. CI does not pin one today (S4, W7).
- **R4.** Rewriting the embedded JS by hand can drop behaviour the webpack output had, such as
  stdout/stderr overriding and exit-when-parent-exits. Fixture tests must cover the boot, render
  and parent-exit paths.
- **R5.** The breaking changes D2–D5 hit downstream users. Release notes need a migration section.

## Acceptance criteria

- Integration tests through the real entry script and Node cover:
  - a CJS `createServerRenderer` module
  - an ESM `.mjs` module with a chunk and top-level await
  - a plain unflagged default-export renderer
  - the D1 Angular path
  - a path with spaces
  - a clear error for an unsupported export
- No webpack, yarn, TypeScript 3 or `new Function` remains. A test checks that the embedded
  resources are byte-equal to the source `.cjs` files.
- `.mjs` and `.cjs` are watched by default. The default regexes match the output of Angular 21's
  `ng build --watch` and `ng serve`.
- `dotnet publish -p:PublishAot=true` of a consuming app gives no trim or AOT warnings from these
  packages.
- `Demo.Web` renders the same HTML as today from `dist/server/main.server.mjs`, with no
  `aspnet-prerendering`.
- CI runs the tests on Node 20, 22 and 24. The coverage gate (95/90) holds.
