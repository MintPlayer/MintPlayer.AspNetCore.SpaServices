# MintPlayer.AspNetCore.SpaServices.Prerendering

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package adds server-side prerendering to an ASP.NET Core single-page application. It runs your SPA's server bundle in Node.js on every incoming HTML request, hands the render the HTML page your pipeline would otherwise have served as a template, and returns the rendered markup instead — so crawlers and first-paint see a fully populated page. It also lets your ASP.NET Core code push request-scoped data (the current user, an entity loaded from the database, a translated string) into the render, so the server-rendered page is not just the empty shell. It is a dependency of [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing), which is the recommended entry point because it supplies the route matching and the service registration helper described below.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Prerendering
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Prerendering
```

## Getting started: a new ASP.NET Core app and a new Angular workspace

These steps go from nothing to a server-rendered Angular page. If you already have one half, skip its step and do the rest. Every step after step 2 changes a file the generators create.

You need the .NET 10 SDK or later, Node.js 20.19+, 22.12+ or 24, and the Angular CLI (`npm install -g @angular/cli`). Angular 19 to 22 are supported.

### 1. Create the ASP.NET Core app

```
dotnet new web -n MyApp
cd MyApp
dotnet add package MintPlayer.AspNetCore.SpaServices.Routing
```

`…SpaServices.Routing` brings in this package together with SpaServices and NodeServices. It also adds route matching, which you need once you supply per-page data (step 9).

### 2. Create the Angular workspace in `ClientApp`

Run this in the project folder, next to `MyApp.csproj`:

```
ng new ClientApp --ssr --skip-git
```

The packages look for the SPA in `ClientApp` by default; see `SpaRoot` under [MSBuild integration](#msbuild-integration) to use another folder. If you already have an Angular workspace without SSR, run `ng add @angular/ssr` inside it instead.

### 3. `ClientApp/angular.json`: set the output path and move the SSR options

Two changes to `projects.ClientApp.architect.build`:

- Add `"outputPath": "dist"`. Without it Angular writes to `dist/ClientApp/browser` and `dist/ClientApp/server`, but every path in this guide is `dist/browser` and `dist/server`.
- **Move** `server`, `outputMode` and `ssr` out of `options` into a new configuration named `ssr`. `ng new --ssr` puts them in `options`. Leave the other options there, such as `security`. While they are there, `ng serve` renders every page itself, so the page this package captures as its template in development is already rendered.

Before (as generated):

```jsonc
"build": {
  "builder": "@angular/build:application",
  "options": {
    "browser": "src/main.ts",
    "tsConfig": "tsconfig.app.json",
    // ...
    "server": "src/main.server.ts",
    "outputMode": "server",
    "ssr": {
      "entry": "src/server.ts"
    }
  },
  "configurations": {
    "production": { /* ... */ },
    "development": { /* ... */ }
  },
  "defaultConfiguration": "production"
}
```

After:

```jsonc
"build": {
  "builder": "@angular/build:application",
  "options": {
    "outputPath": "dist",
    "browser": "src/main.ts",
    "tsConfig": "tsconfig.app.json"
    // ...
  },
  "configurations": {
    "production": { /* ... */ },
    "development": { /* ... */ },
    "ssr": {
      "server": "src/main.server.ts",
      "outputMode": "server",
      "ssr": {
        "entry": "src/server.ts"
      }
    }
  },
  "defaultConfiguration": "production"
}
```

### 4. `ClientApp/src/app/app.routes.server.ts`: render on the server, not at build time

Change `RenderMode.Prerender` to `RenderMode.Server`:

```typescript
import { RenderMode, ServerRoute } from '@angular/ssr';

export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server
  }
];
```

With `RenderMode.Prerender`, `ng build` renders the pages at build time and `dist/browser/index.html` is a rendered page, which is not a usable template. With `RenderMode.Server` the build writes the plain template as `dist/browser/index.csr.html`, which step 7 points to.

### 5. `ClientApp/src/main.server.ts`: export `renderApplication`

Add one line at the end of the generated file:

```typescript
export { renderApplication } from '@angular/platform-server';
```

The generated file exports a bootstrap function, not a renderer. With this export the package can render the bundle itself (see [The Angular side](#the-angular-side)). `@angular/platform-server` is already a dependency of an SSR workspace.

### 6. `ClientApp/package.json`: add the two SSR build scripts

```json
"scripts": {
  "ng": "ng",
  "start": "ng serve",
  "build": "ng build",
  "build:ssr:development": "ng build --configuration development,ssr",
  "build:ssr:production": "ng build --configuration production,ssr",
  "watch": "ng build --watch --configuration development",
  "test": "ng test"
}
```

The host runs `build:ssr:development` in development (step 7). `dotnet publish` runs `build:ssr:production`. Remove the generated `serve:ssr:ClientApp` script: it starts Angular's own Express server, which this setup doesn't use.

### 7. `Program.cs`

Replace the generated file. In particular, the template's `app.MapGet("/", () => "Hello World!")` would answer `/` before the SPA could.

```csharp
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    // The browser half of the build (angular.json "outputPath": "dist").
    configuration.RootPath = "ClientApp/dist/browser";
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
if (!app.Environment.IsDevelopment())
{
    app.UseSpaStaticFilesImproved();
}

app.UseRouting();
// Map your controllers, minimal APIs, etc. here, e.g. app.MapControllers();

// Explicit: endpoints must run here, before the SPA middleware, which answers every request that reaches
// it. Without this call WebApplication runs them at the very end of the pipeline, so they are never reached.
app.UseEndpoints(_ => { });

app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";

    // RenderMode.Server writes the plain template as index.csr.html (step 4). Used outside Development;
    // in Development the Angular dev server answers instead.
    spa.Options.DefaultPage = "/index.csr.html";

    // Registered before UseAngularCliServer, so the page the dev server returns becomes the template.
    spa.UseSpaPrerendering(options =>
    {
        options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.server.mjs";
        // Development: build the server bundle on the first request, and rebuild it when you save.
        // Production: dotnet publish has already built it.
        options.BootModuleBuilder = app.Environment.IsDevelopment()
            ? new AngularPrerendererBuilder("build:ssr:development")
            : null;
        // The Vite dev server's own endpoints are not pages.
        options.ExcludeUrls = ["/@vite", "/@fs", "/@ng", "/@id"];
    });

    if (app.Environment.IsDevelopment())
    {
        spa.UseAngularCliServer(npmScript: "start");
    }
});

app.Run();
```

### 8. `MyApp.csproj` (recommended)

```xml
<PropertyGroup>
  <PublishNodeModules>false</PublishNodeModules>
</PropertyGroup>
```

The application builder bundles everything the server code needs into `dist/server`, so `node_modules` doesn't have to be published. The packages already keep `ClientApp` out of the compile and content items, and run `npm install` before a Debug build when `ClientApp/node_modules` is missing.

### 9. Run it

```
dotnet run
```

Don't start `ng serve` yourself: the middleware starts the dev server and the SSR watch build. The first page request waits for both builds, which can take a while. After that, *View source* shows the rendered markup inside `<app-root>` instead of an empty element.

Next steps:

- **Supply data to the render:** implement `ISpaPrerenderingService` and register it with `AddSpaPrerenderingService<T>()` ([Supplying data](#supplying-data-ispaprerenderingservice)). Then hand the values to Angular with a `prerenderProviders` export ([Handing `OnSupplyData` to Angular](#handing-onsupplydata-to-angular-prerenderproviders)).
- **Publish:** `dotnet publish -c Release` runs `npm run build:ssr:production` and includes `ClientApp/dist` in the output.
- **XSRF tokens for the SPA's API calls:** [MintPlayer.AspNetCore.SpaServices.Xsrf](../MintPlayer.AspNetCore.SpaServices.Xsrf/README.md).

## How it works

`UseSpaPrerendering` is registered inside the `UseSpaImproved` callback, which makes it a piece of middleware that sits *in front of* everything the SPA middleware serves — including the static-file middleware that serves `index.html` in production and the Angular CLI dev-server proxy in development.

Because it sits in front, it can do something a normal middleware does not: it temporarily swaps `HttpResponse.Body` for an in-memory buffer, calls the rest of the pipeline, and keeps whatever the downstream wrote. That captured HTML — normally your unrendered `index.html` shell — is then passed to Node as `originalHtml`, and your prerendering code uses it as the document to render into. The response the client finally receives is the render's output, not the captured page.

The pipeline order that makes this work is therefore:

1. `UseSpaImproved` is registered last in `Configure`, after routing and endpoints.
2. Inside its callback, `spa.UseSpaPrerendering(...)` is registered **before** `spa.UseAngularCliServer(...)` (development) — and, in production, before the SPA's default-page static file handler.
3. Requests for URLs your MVC endpoints or static-file middleware already handle never reach the prerenderer.

Before calling downstream, the middleware strips the headers that would make the downstream answer with something other than the whole page: the conditional-request headers (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`, `If-Range`), `Range`, and `Accept-Encoding`. Otherwise the capture could be a `304`, a `206` slice, or a gzip stream rather than a full `200` body.

`Accept-Encoding` is restored after the capture, because upstream compression middleware reads it when the response starts. `Range` is not restored: nothing downstream of the capture reads it, and a prerendered body cannot satisfy a byte range in any case.

A request is only prerendered when **all** of these hold:

- the request method is `GET`;
- its path does not start with any prefix in `ExcludeUrls`;
- the client has not aborted the request;
- the captured `Content-Type` media type is `text/html` (matched case-insensitively, parameters such as `; charset=utf-8` ignored);
- the captured response status is **exactly `200`**, and carries no `Content-Range` header;
- the captured response is not content-encoded (no `Content-Encoding`, or `identity`);
- if the response declared a `Content-Length`, that many bytes were actually captured;
- the captured bytes are valid UTF-8, and the decoded template contains no NUL characters;
- the captured body is not empty or whitespace;
- `HttpContext.SkipPrerendering()` was not called for the request.

Otherwise the captured bytes are copied through to the real response stream unchanged — so a JSON API response, a `404`, or a CSS file passes through untouched. Not being `text/html` is not an error; it is the normal path for static content in development.

Everything after the content-type check is one requirement stated five ways: **the capture has to be the complete document.** Being `text/html` with a successful status does not establish that — a `Range: bytes=0-0` request makes the static-file middleware answer `206` with a single byte of `index.html`, which is `text/html` and 2xx and would otherwise have been used as the template. The middleware therefore strips `Range` from the request before capturing (alongside the conditional headers), and rejects a partial representation if one arrives anyway from a source it does not control, such as a dev-server proxy.

One consequence worth knowing: because the header is removed before the downstream sees it, a `Range` request is answered with the whole prerendered page rather than a slice, and an *unsatisfiable* range no longer produces a `416` either. A server is permitted to ignore `Range`, and a prerendered body — generated per request — has no stable byte range to serve.

### `GET` only

Prerendering applies to `GET`. Other methods are passed straight through, before the server bundle is built, so a `POST` or `OPTIONS` to a SPA route does not wait for a build it has no use for.

A `HEAD` is included in that: the static-file middleware answers a `HEAD` with the headers a `GET` would return and no body, so there is no template to render. Its `Content-Length` still reports the length of the document the equivalent `GET` would return, as it should.

## Basic setup

The `Program.cs` in [step 7](#7-programcs) is the complete setup. Mind the order there:

1. `UseSpaStaticFilesImproved()` (outside Development).
2. `UseRouting()`, then your endpoints, then an explicit `UseEndpoints(_ => { })`.
3. `UseSpaImproved(...)` last, with `UseSpaPrerendering` registered **before** `UseAngularCliServer`.

`UseSpaImproved` answers every request that reaches it. With `WebApplication`, endpoints you map run at the end of the pipeline unless you call `UseEndpoints` yourself, so leave out step 2's explicit call and your controllers are never reached.

The same pipeline in a classic `Startup.Configure`:

```csharp
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    if (!env.IsDevelopment())
    {
        app.UseSpaStaticFilesImproved();
    }

    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller}/{action=Index}/{id?}");
    });

    app.UseSpaImproved(spa =>
    {
        spa.Options.SourcePath = "ClientApp";
        // "outputMode": "server" with RenderMode.Server writes the template as index.csr.html.
        spa.Options.DefaultPage = "/index.csr.html";

        // Registered before UseAngularCliServer, so the dev server's index.html
        // becomes the template this middleware captures.
        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.server.mjs";
            options.BootModuleBuilder = env.IsDevelopment()
                ? new AngularPrerendererBuilder("build:ssr:development")
                : null;
            // Vite dev-server endpoints, not pages.
            options.ExcludeUrls = ["/@vite", "/@fs", "/@ng", "/@id"];
        });

        if (env.IsDevelopment())
        {
            spa.UseAngularCliServer(npmScript: "start");
        }
    });
}
```

## `SpaPrerenderingOptions`

Namespace: `MintPlayer.AspNetCore.SpaServices.Prerendering`.

| Member | Type | Default | Meaning |
| --- | --- | --- | --- |
| `BootModulePath` | `string` | none — **required** | Path, relative to the application content root, of the JavaScript file containing your prerendering logic (the server bundle). For an Angular application-builder app this is `ClientApp/dist/server/main.server.mjs` (an ES module; see [Boot module formats](#boot-module-formats)). Leaving it null or empty makes `UseSpaPrerendering` throw an `InvalidOperationException` at startup. |
| `BootModuleBuilder` | `ISpaPrerendererBuilder?` | `null` | An optional builder invoked once, lazily, on the first request that needs the boot module, to generate it. Intended for **development only** — set it to `null` in production, where the bundle is produced at publish time. Use `AngularPrerendererBuilder` (see below). |
| `ExcludeUrls` | `string[]` | `null` (treated as empty) | URL prefixes for which prerendering is skipped entirely; matched with `PathString.StartsWithSegments`. Use it to keep dev-server plumbing and client-side asset folders from being answered with a prerendered page. |
| `NodePath` | `string` | `"node"` | The Node executable used to host the render. Set an absolute path if `node` is not on the host's `PATH`. |
| `TimeoutMilliseconds` | `int` | `0` | The **per-page render timeout**, passed through to the prerendering JavaScript for a single render call. `0` means the default of 30 seconds; `-1` means wait indefinitely. See the note below. |
| `OnPrepareResponse` | `Func<HttpContext, Task>` | `null` | Invoked from a `Response.OnStarting` callback, i.e. after the prerendering logic has run and just before the response starts. The usual place to add response headers. |

### `TimeoutMilliseconds` is a render timeout, not a build timeout

`TimeoutMilliseconds` bounds one Node render of one page. It has nothing to do with how long `BootModuleBuilder` is allowed to spend building your server bundle — earlier XML documentation on this property said otherwise, so it is worth stating plainly.

The time allowed for the **build** is `SpaOptions.StartupTimeout` (default 120 seconds), set on the SPA builder:

```csharp
app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";
    spa.Options.StartupTimeout = TimeSpan.FromMinutes(5); // a slow SSR build

    spa.UseSpaPrerendering(options =>
    {
        options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.server.mjs";
        options.BootModuleBuilder = new AngularPrerendererBuilder("build:ssr:development");
        options.TimeoutMilliseconds = 60_000; // and a slow page render
    });
});
```

## `AngularPrerendererBuilder`

`AngularPrerendererBuilder` implements `ISpaPrerendererBuilder`. It runs an npm script from your `SpaOptions.SourcePath` folder with `--watch`, streams its output to the logger under the category `AngularPrerendererBuilder`, and waits until the build has signalled success before the first request is prerendered. Because it runs in watch mode, later edits to your SPA rebuild the server bundle without restarting the host.

```csharp
// The application builder (Angular 17+): waits for "Application bundle generation complete." once.
new AngularPrerendererBuilder(npmScript: "build:ssr:development");

// The legacy webpack builders (`ng build && ng run ClientApp:server`) print "Build at:" once per build.
new AngularPrerendererBuilder(
    npmScript: "build:ssr:development",
    finishedRegex: @"Build at\:",
    finishedRegexNumber: 2);
```

| Constructor parameter | Meaning |
| --- | --- |
| `npmScript` | Name of the script in your `package.json` that builds the server-side bundle. It is run with `--watch` appended, so it must be a single command (`ng build --configuration development,ssr`); in `a && b` only `b` receives the flag. Throws `ArgumentException` if null or empty. |
| `finishedRegex` | Regular expression whose appearance on stdout means a build completed. Defaults to `Application bundle generation complete\.`. |
| `finishedRegexNumber` | How many times the pattern must appear before the build counts as done. Defaults to `1`: the application builder builds the browser and server bundles in one pass. |

Earlier releases defaulted to `Build at\:` and `2`, for the webpack browser-then-server script. Pass those explicitly if you still use it — otherwise the first request waits for a success line that never comes, until `SpaOptions.StartupTimeout` elapses.

The bundle is built **once** per process; every concurrent request awaits the same build. A build that fails, exits without signalling success, or times out is final: the resulting `InvalidOperationException` includes the npm script's own stdout and stderr, and the failure is reported to every request rather than only the first. A failed build is not retried, because retrying would spawn another npm process and a hanging build does not fix itself.

The npm package manager command used is `SpaOptions.PackageManagerCommand`.

## Supplying data: `ISpaPrerenderingService`

`MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService` is how your ASP.NET Core code participates in a render. It has two members:

```csharp
public interface ISpaPrerenderingService
{
    Task BuildRoutes(ISpaRouteBuilder routeBuilder);
    Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data);
}
```

- **`BuildRoutes`** declares your SPA's route table to the server, mirroring your Angular router configuration. The Routing package's `ISpaRouteService` uses it to work out which named route the current request matches, and to generate URLs for redirects. Routes are built once and cached.
- **`OnSupplyData`** is called on every request that is about to be prerendered, after the template has been captured. The `data` dictionary already contains `originalHtml`; everything you add to it is JSON-serialized and reaches the render as `params.data`.

The service is registered **scoped**, so it can take constructor dependencies on your own scoped services. Inside `OnSupplyData`, request-scoped services are also reachable through `httpContext.RequestServices`:

```csharp
var personService = httpContext.RequestServices.GetRequiredService<IPersonService>();
```

### Registration

The registration helper lives in the Routing package (`MintPlayer.AspNetCore.SpaServices.Routing`), because a prerendering service's `BuildRoutes` is only meaningful together with the route services:

```csharp
// Program.cs
using MintPlayer.AspNetCore.SpaServices.Routing;

builder.Services.AddSpaPrerenderingService<DemoSpaPrerenderingService>();
```

`AddSpaPrerenderingService<TService>` registers `TService` as a scoped `ISpaPrerenderingService` and also adds `IHttpContextAccessor` and the SPA route services. If no `ISpaPrerenderingService` is registered, prerendering still works — the render simply receives nothing but `originalHtml`.

### A complete worked example

```csharp
using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;

public class DemoSpaPrerenderingService : ISpaPrerenderingService
{
    private readonly ISpaRouteService spaRouteService;
    private readonly IPersonService personService;

    public DemoSpaPrerenderingService(ISpaRouteService spaRouteService, IPersonService personService)
    {
        this.spaRouteService = spaRouteService;
        this.personService = personService;
    }

    // Mirrors the Angular router configuration. Group names are prefixed onto
    // child route names, so "person" + "show" is addressed as "person-show".
    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("", "home")
            .Group("person", "person", person_routes => person_routes
                .Route("", "list")
                .Route("create", "create")
                .Route("{personid}", "show")
                .Route("{personid}/edit", "edit")
            );

        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
    {
        var route = await spaRouteService.GetCurrentRoute(context);
        switch (route?.Name)
        {
            case "home":
                // Redirect instead of rendering. ISpaRouteService.Redirect calls
                // SkipPrerendering() for you, so no page is rendered and discarded.
                await spaRouteService.Redirect(context, "person-list", new Dictionary<string, object>());
                break;

            case "person-list":
                data["people"] = await personService.GetPeople();
                break;

            case "person-show":
            case "person-edit":
                {
                    var personid = Convert.ToInt32(route.Parameters["personid"]);
                    var person = await personService.GetPerson(personid);
                    if (person == null)
                    {
                        // A status assigned from an OnStarting callback is invisible to the
                        // middleware's own status check, so say so explicitly.
                        context.SkipPrerendering();
                        context.Response.OnStarting(() =>
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return Task.CompletedTask;
                        });
                    }
                    else
                    {
                        data["person"] = person;
                    }
                }
                break;
        }

        data["message"] = "Message from server";
    }
}
```

Everything you put in `data` must be JSON-serializable — it crosses a process boundary to Node. Keep it to what the first render needs; a large payload is serialized, transferred and parsed on every request.

## The Angular side

Start from the workspace `ng new --ssr` (or `ng add @angular/ssr`) generates; Angular 19 to 22 work. The stock `src/main.server.ts` exports a bootstrap function, not a renderer, so add **one line** that lets this package render the bundle itself:

```typescript
// ClientApp/src/main.server.ts
import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { config } from './app/app.config.server';

const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);

export default bootstrap;

// The one addition.
export { renderApplication } from '@angular/platform-server';
```

The prerenderer then calls `renderApplication(bootstrap, { document, url, platformProviders })` with:

- **`document`**: `params.data.originalHtml`, the HTML template the middleware captured. The render fills in this real page — with its `<head>`, its stylesheet and script tags, its `<base href>` — instead of a synthetic one. A missing or empty template is what produces Angular's `NG05104`.
- **`url`**: the request's path and query. It is relative on purpose: Angular 19.2+ rejects absolute URLs whose host is not in `security.allowedHosts` (`NG05706`).
- **`platformProviders`**: whatever your optional `prerenderProviders` export returns.

### Handing `OnSupplyData` to Angular: `prerenderProviders`

Optional: without this export the page still renders, Angular just doesn't receive any server data. Everything your `OnSupplyData` added arrives in `data`. Export `prerenderProviders` to turn it into providers, so components inject the values instead of issuing an HTTP call the server has already made. The render itself makes no outbound requests: data flows in from ASP.NET Core.

```typescript
import { APP_BASE_HREF } from '@angular/common';
import { StaticProvider } from '@angular/core';
import { MESSAGE_TOKEN, PERSON_TOKEN, PEOPLE_TOKEN } from './app/tokens';

export const prerenderProviders = (data: any, params: { baseUrl: string }): StaticProvider[] => [
  // Relative: an absolute base href (origin + path) makes Angular reject the URL as changing origin (NG05703).
  { provide: APP_BASE_HREF, useValue: params.baseUrl },
  { provide: MESSAGE_TOKEN, useValue: data.message },
  // Route-dependent values: provide null when absent.
  { provide: PEOPLE_TOKEN, useValue: data.people ?? null },
  { provide: PERSON_TOKEN, useValue: data.person ?? null },
];
```

It receives `(data, params)`, where `params` has `location`, `origin`, `url`, `baseUrl`, `absoluteUrl` and `data`.

These are *platform* providers. A provider for the same token in `app.config.server.ts` (or `app.config.ts`) is closer to the components and wins, so do not also provide the token there. Keep `APP_BASE_HREF` relative: an absolute base href such as `params.origin + ...` makes Angular reject the navigation as changing origin (`NG05703`).

`MESSAGE_TOKEN`, `PERSON_TOKEN` and `PEOPLE_TOKEN` come from the demo; declare your own. They are ordinary Angular `InjectionToken`s, declared once and given a client-side fallback in your browser config:

```typescript
// ClientApp/src/app/tokens.ts
import { InjectionToken } from '@angular/core';
import { Person } from './entities/person';

export const MESSAGE_TOKEN = new InjectionToken<string>('MESSAGE');
export const PERSON_TOKEN = new InjectionToken<Person>('PERSON');
export const PEOPLE_TOKEN = new InjectionToken<Person[]>('PEOPLE');
```

### Workspace configuration

Steps [3](#3-clientappangularjson-set-the-output-path-and-move-the-ssr-options), [4](#4-clientappsrcappapproutesserverts-render-on-the-server-not-at-build-time) and [6](#6-clientapppackagejson-add-the-two-ssr-build-scripts) of the getting-started guide make these changes to a generated workspace. In short:

- **`"outputPath": "dist"`** in the build options, so the output lands in `dist/browser` and `dist/server` rather than `dist/<project-name>/...`.
- **`RenderMode.Server` for every route** in `src/app/app.routes.server.ts` (`ng new --ssr` generates `RenderMode.Prerender`). Then nothing is prerendered at build time and `dist/browser/index.csr.html` stays a plain template; set `SpaOptions.DefaultPage` to it.

```typescript
// ClientApp/src/app/app.routes.server.ts
export const serverRoutes: ServerRoute[] = [
  { path: '**', renderMode: RenderMode.Server }
];
```

- **Move** `server`, `outputMode` and `ssr` out of the default build options into a configuration of their own. Angular's dev server renders pages itself whenever the build it serves has `server` and `outputMode`, and then the "template" the middleware captures from `ng serve` would already be a rendered page:

```jsonc
// angular.json, projects.ClientApp.architect.build
"options": {
  "outputPath": "dist",
  // ... no server, outputMode or ssr here
},
"configurations": {
  "production": { /* ... */ },
  "development": { /* ... */ },
  "ssr": {
    "server": "src/main.server.ts",
    "outputMode": "server",
    "ssr": { "entry": "src/server.ts" }
  }
}
```

The `package.json` scripts referenced by `BuildServerSideRenderer` and `AngularPrerendererBuilder`, each a single command:

```json
{
  "scripts": {
    "start": "ng serve",
    "build": "ng build",
    "build:ssr:development": "ng build --configuration development,ssr",
    "build:ssr:production": "ng build --configuration production,ssr"
  }
}
```

### Boot module formats

`BootModulePath` may point at any of these. The module is loaded with a dynamic `import()`, so an ES module may import its chunks and use top-level `await`.

| Bundle | Detected by | Called as |
| --- | --- | --- |
| `createServerRenderer(...)` from `aspnet-prerendering`, CommonJS or ESM | the function's flag | unchanged, as before |
| Angular application-builder entry (`main.server.mjs`) | a default export plus a `renderApplication` export | `renderApplication(default, { document, url, platformProviders })` |
| A plain renderer function, the default export (or `renderToString`) | a function export | `fn(params)`, returning (a promise of) the object below |

An Angular bundle without the `renderApplication` export fails with an error that quotes the line to add.

Node caches ES modules for the life of the process and offers no way to evict them. When the bundle is rebuilt, the file watcher of `MintPlayer.AspNetCore.NodeServices` restarts Node, which is what loads the new bundle; `.mjs` and `.cjs` are among the watched extensions.

### What your renderer may return

The object your promise resolves to maps onto these fields:

| Field | Effect |
| --- | --- |
| `html` | The complete HTML page to send. The response is cleared, `Content-Type` is set to `text/html`, and this is written as the body. Required unless `redirectUrl` is set — returning neither throws an `InvalidOperationException` naming the problem. |
| `redirectUrl` | Issues a redirect instead of writing a body. Combined with `statusCode` `301` or `308` the redirect is permanent; anything else is treated as temporary. |
| `statusCode` | Applied to the response before the body is written. |
| `globals` | **Not supported** with `UseSpaPrerendering`; setting it throws. Embed anything you want to hand the client into the HTML page you return. |

## Skipping prerendering for a request: `HttpContext.SkipPrerendering()`

```csharp
using MintPlayer.AspNetCore.SpaServices.Prerendering;

context.SkipPrerendering();          // opt this request out
var skipped = context.IsPrerenderingSkipped(); // query the flag
```

The middleware decides whether to prerender by reading `HttpResponse.StatusCode` at the moment the captured response comes back. A status code assigned from inside a `Response.OnStarting` callback is **not yet visible at that point** — the callback only runs later, when the response actually starts. So code that defers its status that way is invisible to the check: the page is prerendered at full cost and the rendered body is then overwritten, or discarded, by whatever the callback does.

Call `SkipPrerendering()` whenever your code decides, during the request, that the response will not be a `2xx` HTML page but assigns that outcome from an `OnStarting` callback. The typical cases are a redirect and a deferred `404`, as in the worked example above.

You do **not** need to call it when:

- you set `Response.StatusCode` directly (the middleware sees it);
- you call `ISpaRouteService.Redirect(...)` — both overloads call `SkipPrerendering()` for you.

**Limitation, stated honestly:** this is an opt-in signal, not a detection mechanism. A deferred status change made by code that does not call `SkipPrerendering()` remains undetectable to the middleware — there is no general way to observe an `OnStarting` callback's effect before it runs. If a third-party middleware or a callback you do not control defers its status, its page will still be prerendered and thrown away.

## Behaviour when the client disconnects

If `HttpContext.RequestAborted` is already cancelled when the captured response comes back, prerendering is skipped and the captured bytes are passed through to the response stream as-is.

This is not merely an optimisation. The middleware downstream of the prerenderer swallows the cancellation — ASP.NET Core's static-file handling catches `OperationCanceledException` and only logs it — so from the prerenderer's vantage point an aborted request is indistinguishable from a successful one: status `200`, `Content-Type: text/html`, `Content-Length` set, and a body that was never written. Handing that empty template to Node is exactly what surfaces as Angular's `NG05104`. Checking the abort flag turns a confusing render error into a request that simply ends.

The captured buffer is copied out rather than dropped, because an abort can also land *after* the body was fully captured, in which case the buffer holds a complete page and discarding it would achieve nothing. When the buffer is empty the copy costs nothing.

## Cancellation of an in-flight render

The render is cancelled when either the client aborts the request or the host begins shutting down; the two tokens are linked for the duration of one render.

Be clear about what this buys you: cancelling **releases the .NET request thread**, it does not stop Node. The RPC protocol between ASP.NET Core and the Node host has no abort channel, so the Node process finishes the render regardless and its result is discarded. Cancellation reclaims the ASP.NET Core side of the request, not the CPU your SSR render is spending.

The same distinction applies at shutdown: a host that is stopping stops waiting for renders, but Node may still be finishing them.

## Logging

The middleware logs under the category **`MintPlayer.AspNetCore.SpaServices.Prerendering.SpaPrerenderingExtensions`**, so it can be filtered by namespace like any other component.

| Level | When |
| --- | --- |
| `Information` | Once, when the server boot module build starts (`"Building server BootModule"`). |
| `Debug` | The request was not a `GET`, or its status carries no response body (`204`, `205`, `304`), or the client aborted. All three are benign, so none of them warns. The abort line includes the bytes actually captured against the `Content-Length` the downstream declared — the gap between the two is the diagnosis. |
| `Warning` | The capture could not be used as a template: a partial representation (status other than `200`, or a `Content-Range` header — the line names the method, the status and the `Content-Range`), a `Content-Encoding` this middleware cannot undo, fewer bytes captured than declared, bytes that are not valid UTF-8, a decoded template containing NUL characters, or an empty template. Each of these means prerendering silently stopped happening for that request, which is why they are visible at default level. |
| `Warning`, then `Debug` | The template has no `<html>` opening tag. This is **not** a rejection — a fragment template is supported, and the render normally succeeds — but if prerendering is misbehaving it is the first thing to check. Warned once per pipeline and logged at `Debug` thereafter, so a deliberate fragment deployment does not warn on every request. Includes the first 200 characters of the template. |

Your build script's own output is logged separately under the category **`AngularPrerendererBuilder`**.

To see the `Debug` messages, raise the level for that category in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "MintPlayer.AspNetCore.SpaServices.Prerendering": "Debug",
      "AngularPrerendererBuilder": "Debug"
    }
  }
}
```

## Troubleshooting

### `NG05104: Angular Universal cannot find a document to render into`

Angular was handed an empty or missing document. Check, in order:

1. If you render yourself (a plain renderer or `createServerRenderer`), you pass `params.data.originalHtml` as the `document` option — not `''`, not a hand-written string, not an omitted option. With the `renderApplication` export this package passes it for you.
2. The response captured from the downstream pipeline is not empty. If it is, a `Warning` under the `MintPlayer.AspNetCore.SpaServices.Prerendering` category names the path and the captured-versus-declared byte counts; enable the logging above.
3. In production, `SpaStaticFilesOptions.RootPath` points at the folder that actually contains `index.html`. Angular 17+ splits its output, so the browser assets sit in `dist/browser`, not `dist` — pointing at `dist` means there is no default page to capture.

An aborted request no longer produces this: it is skipped, with a `Debug` message. Neither does a `Range` request, a `HEAD`, or a response in a non-UTF-8 charset - see the conditions list above.

### The first request hangs, or the build never finishes

The server bundle is built once, lazily, on the first request that needs it, and every request awaits that same build. The wait is bounded by `SpaOptions.StartupTimeout` (default 120 seconds). On expiry you get an `InvalidOperationException` naming the package-manager command, the npm script, the timeout that elapsed, and the script's own stdout and stderr — read the npm output in the message first, it usually says what went wrong.

Two adjacent failure shapes:

- **The script exited without signalling success.** The exception says so and includes the output; usually a genuine build error.
- **The build succeeded but the pattern never matched the expected number of times.** Check `finishedRegex` and `finishedRegexNumber`: the defaults fit the application builder (`Application bundle generation complete.`, once); a webpack browser-then-server script needs `Build at\:` and `2`.

If the build legitimately takes longer than two minutes, raise `spa.Options.StartupTimeout`. Note that a failed build is not retried — restart the host after fixing it.

### `Prerendering timed out after 30000ms`

Angular reports most render errors (an unmatched route `NG04002`, `NG05703`, a component that throws) to its `ErrorHandler`, which logs them, and the render promise then never settles. The timeout is what ends the request. Look in the Node output logged under `MintPlayer.AspNetCore.NodeServices` for the `ERROR RuntimeError: NG0...` line that precedes it.

### Prerendering silently does nothing

The response comes back as the unrendered shell, and no error appears. Work through the conditions the middleware requires:

- **Request method** — must be `GET`. A `HEAD`, `POST` or `OPTIONS` is passed straight through.
- **`ExcludeUrls`** — is the request path under one of the excluded prefixes?
- **Content type** — the captured `Content-Type` media type must be `text/html`. A downstream that answers with `application/octet-stream`, or with no `Content-Type` at all, is passed through. (Matching is case-insensitive and ignores parameters, so `TEXT/HTML` and `text/html ; charset=utf-8` both qualify.)
- **Status code** — must be exactly `200`. A `304`, a `404`, a redirect or a `206 Partial Content` is passed through. Conditional-request headers and `Range` are stripped precisely so a `304` or a `206` is not what gets captured.
- **Partial or encoded capture** — a response carrying `Content-Range`, a `Content-Encoding` other than `identity`, or fewer bytes than its own `Content-Length` declared, is skipped with a `Warning`. Each of these means the capture is not the whole document.
- **Not valid UTF-8** — the template is decoded as UTF-8 regardless of the declared charset, so a response in another charset (`text/html; charset=utf-16`, say) is skipped with a `Warning`.
- **Empty body** — an empty captured template is skipped with a `Warning`.
- **`SkipPrerendering()`** — did your own code, or `ISpaRouteService.Redirect`, opt this request out?
- **Pipeline order** — is `UseSpaPrerendering` registered before the middleware that serves the page (the CLI server in development, the static-file default page in production)? Registered after, it never sees a template.
- **MVC endpoints** — a path matched by an MVC route is answered before the SPA middleware runs at all.

### A redirect returns a prerendered page body

The redirect's status was assigned from a `Response.OnStarting` callback, which the middleware cannot see, so the page was prerendered and the render's output collided with the redirect. Call `HttpContext.SkipPrerendering()` alongside the redirect — or use `ISpaRouteService.Redirect`, which already does.

### An error page gets prerendered instead of your app

If `UseExceptionHandler`, `UseStatusCodePagesWithReExecute` or similar is registered **inside** the `UseSpaImproved` callback, it sits inside the capture — so when it handles a downstream failure, its error page becomes what this middleware captures and hands to the renderer. That page is a complete, well-formed `text/html` document, so it passes every check the middleware makes; the render then fails with `NG05104` because the page contains no application root element.

Register error handling **outside** the SPA callback, near the top of `Configure`, as in the Basic setup above. If you need a template assertion that is specific to your app, the place for it is your own `OnSupplyData`:

```csharp
public Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
{
    var template = (string)data["originalHtml"];

    // The middleware cannot check this: it does not know your root element's name.
    if (!template.Contains("<my-app", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The SSR template has no application root element.");
    }

    return Task.CompletedTask;
}
```

### `Globals is not supported when prerendering via UseSpaPrerendering()`

Your renderer returned a `globals` object. It exists only for backwards compatibility and is meaningless when the render returns a complete HTML page. Embed the data in the HTML you return instead.

### `Prerendering returned no HTML`

Your renderer's promise resolved without `html` and without `redirectUrl`. Return one of them.

## MSBuild integration

This package depends on [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices), which automatically imports build targets that run `npm install` and build your SPA as part of `dotnet build` and `dotnet publish`.

| Property | Default | Meaning |
| --- | --- | --- |
| `EnableSpaBuilder` | `true` | Master switch for all SPA build automation. Set to `false` in a project that references the package but has no SPA. |
| `SpaRoot` | `ClientApp\` | The SPA source folder, relative to the project. Also controls which files are excluded from compilation and shown as `None` items. |
| `BuildServerSideRenderer` | `true` | When `true`, publish runs `npm run build:ssr:production` and the SSR bundle is included in the publish output (plus `node_modules`, unless `PublishNodeModules` is `false`). When `false`, publish runs `npm run build -- --configuration production` and no server bundle is produced — leave it `true` when you use this package. |
| `PublishNodeModules` | `$(BuildServerSideRenderer)` | Copies `node_modules` into the publish output next to the server bundle. The Angular application builder bundles server dependencies into `dist/server`, so such apps can set it to `false` and publish far less. |

Note the script names: with `BuildServerSideRenderer` enabled, your `package.json` must define `build:ssr:production`.

### Disabling the SPA builder

If your project references this package but does not contain a SPA:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

### Pointing at a different SPA folder

```xml
<PropertyGroup>
  <SpaRoot>src\frontend\</SpaRoot>
</PropertyGroup>
```

The trailing separator matters — the property is concatenated with paths such as `$(SpaRoot)node_modules`.

See the [NodeServices package](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) for the remaining properties, including SPA build caching and npm-workspace support.

## Related Packages

- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration and `AddSpaPrerenderingService` (recommended entry point)
- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services (`UseSpaImproved`, `SpaOptions`, `UseAngularCliServer`)
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - Shared abstractions (`ISpaBuilder`, `ISpaPrerendererBuilder`)
- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration and MSBuild targets

## License

This project is licensed under the Apache 2.0 License.
