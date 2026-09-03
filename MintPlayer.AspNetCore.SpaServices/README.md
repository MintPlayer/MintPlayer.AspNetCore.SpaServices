# MintPlayer.AspNetCore.SpaServices

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package hosts a Single Page Application (Angular, React, or anything else that builds to static files) from an ASP.NET Core application: it serves the SPA's built assets in Production, starts and proxies the framework's own development server in Development, and rewrites unmatched requests to the SPA's default page so client-side routing works. It is the foundation the MintPlayer prerendering and routing packages build on, and it can be used entirely on its own if you only want hosting plus a dev-server integration without server-side rendering.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices
```

## How this differs from Microsoft.AspNetCore.SpaServices

This package started as a fork of Microsoft's SPA services and keeps the same shape (an `ISpaBuilder`, a static-file provider, a dev-server proxy), but it is maintained independently, targets current .NET, and adds the extension points the MintPlayer prerendering stack needs — notably a configurable set of "dev server is ready" regexes, a bounded startup timeout that also covers the SSR bundle build, and proxy behaviour that treats a client disconnect as a normal outcome rather than an error.

### Why the method names end in `Improved`

The public entry points are deliberately named differently from Microsoft's:

| This package | Microsoft's package |
|---|---|
| `app.UseSpaImproved(...)` | `app.UseSpa(...)` |
| `services.AddSpaStaticFilesImproved(...)` | `services.AddSpaStaticFiles(...)` |
| `app.UseSpaStaticFilesImproved()` | `app.UseSpaStaticFiles()` |

Both packages declare their extension methods on `IApplicationBuilder` / `IServiceCollection`, and both use namespaces that a typical web project has in scope. If the names were identical, adding a `using` (or an implicit global using from another package) could silently bind your call to the *other* implementation, and you would get Microsoft's `SpaOptions` and Microsoft's proxy while reading this documentation. The distinct names make that mistake impossible: `UseSpaImproved` can only ever be this package's method, and the compiler tells you immediately if the namespace is missing.

All of this package's extension methods live in:

```csharp
using MintPlayer.AspNetCore.SpaServices.Extensions;
```

The `ISpaBuilder` / `ISpaOptions` abstractions live in `MintPlayer.AspNetCore.SpaServices.Abstractions`, and `SpaOptions` itself in `MintPlayer.AspNetCore.SpaServices.Core`.

## Setting up

The setup has three parts, in the order you write them.

### 1. Register the static-file provider (`ConfigureServices`)

```csharp
using MintPlayer.AspNetCore.SpaServices.Extensions;

public void ConfigureServices(IServiceCollection services)
{
    services.AddControllersWithViews();

    services.AddSpaStaticFilesImproved(configuration =>
    {
        // Angular 17+ writes browser assets to dist/browser - see the gotcha below.
        configuration.RootPath = "ClientApp/dist/browser";
    });
}
```

### 2. Serve the built assets outside Development (`Configure`)

```csharp
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
```

In Development there is no `dist` folder on disk, because the SPA is served from the dev server instead — so the call is skipped.

### 3. Host the SPA, last in the pipeline

`UseSpaImproved` must come after routing, MVC, and anything else that can handle a request, because it ends the pipeline: whatever reaches it is treated as a client-side route and answered with the SPA's default page.

```csharp
app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";

    if (env.IsDevelopment())
    {
        spa.UseAngularCliServer(npmScript: "start");
    }
});
```

### The `ISpaBuilder` callback

The callback receives an `ISpaBuilder` with exactly two members:

| Member | Type | Purpose |
|---|---|---|
| `Options` | `ISpaOptions` | The options for this SPA. Pre-populated from `IOptions<SpaOptions>` in DI, then **cloned**, so several `UseSpaImproved` calls in one app never interfere with each other. |
| `ApplicationBuilder` | `IApplicationBuilder` | The pipeline the SPA is hosted in. Use it to register middleware that should run *inside* the SPA scope, e.g. `spa.ApplicationBuilder.UseResponseCaching()`. |

Everything you register inside the callback runs before the default-page middleware, which `UseSpaImproved` attaches for you after the callback returns.

Because `Options` is cloned from DI, you can also set defaults centrally and keep the callback short:

```csharp
services.Configure<MintPlayer.AspNetCore.SpaServices.Core.SpaOptions>(options =>
{
    options.SourcePath = "ClientApp";
    options.StartupTimeout = TimeSpan.FromMinutes(5);
});
```

## `SpaOptions`

`MintPlayer.AspNetCore.SpaServices.Core.SpaOptions` implements `ISpaOptions`. The properties on the interface are the ones reachable through `spa.Options` inside the callback; `CliRegexes` exists only on the concrete class, so it can only be set through `services.Configure<SpaOptions>`.

| Member | Type | Default | Meaning |
|---|---|---|---|
| `DefaultPage` | `PathString` | `"/index.html"` | The page every unmatched request is rewritten to. Setting it to `null` or `""` throws `ArgumentException`. |
| `DefaultPageStaticFileOptions` | `StaticFileOptions?` | `null` | Static-file options used when serving the default page. Set its `FileProvider` to serve `index.html` from somewhere other than the registered SPA root — this is how you host more than one SPA with distinct default pages. When `null`, a fresh `StaticFileOptions` is used and the registered `AddSpaStaticFilesImproved` provider (or `wwwroot`) supplies the file. |
| `SourcePath` | `string?` | `null` | Path, relative to the application working directory, of the SPA source folder (e.g. `"ClientApp"`). Required by `UseAngularCliServer`, which throws `InvalidOperationException` if it is empty. It may legitimately not exist in a published application. |
| `DevServerPort` | `int` | `0` | Port the development server should listen on. `0` (the default) means a free TCP port is picked at startup and passed to the CLI as `--port`. Set it only if you need a fixed, predictable port. |
| `PackageManagerCommand` | `string` | `"npm"` | Executable used to run your `package.json` scripts — e.g. `"yarn"` or `"pnpm"`. Setting it to `null` or `""` throws `ArgumentException`. On Windows it is invoked through `cmd /c` so stdio can be captured. |
| `StartupTimeout` | `TimeSpan` | `120` seconds | Maximum time a request waits for the SPA to become ready. See the callout below. |
| `CliRegexes` | `Regex[]?` | `null` | Regexes the dev-server integration waits for on stdout; each must contain a named group `openbrowser` that captures the URL the dev server is listening on. Only present on `SpaOptions`, not on `ISpaOptions`. In practice, prefer passing the regexes to `UseAngularCliServer` (see below) — that argument is what the Angular CLI middleware actually reads. |

### Callout: `StartupTimeout`

`StartupTimeout` is the single knob for "how long may starting up take before a request gives up". It bounds two different waits:

- **The dev server.** The first request after startup waits for the Angular CLI to print its ready line. Each request gets its own timeout, so a request that times out does not poison later ones.
- **The server-side rendering bundle build.** When you use `MintPlayer.AspNetCore.SpaServices.Prerendering` with a boot-module builder, the wait for the SSR build to report success is bounded by this same value. Before, nothing bounded it and a build that never reported success left the first request hanging forever.

The default of 120 seconds is comfortable on a warm developer machine and frequently *not* enough on a cold CI agent or the first run after `npm install`, where a full Angular build plus SSR build can take several minutes. If you see either of these:

```
The Angular CLI process did not start listening for requests within the timeout period of 120 seconds.
```
```
The npm script 'build:ssr:development' did not indicate success within the timeout period of 120 seconds.
```

raise the timeout rather than retrying:

```csharp
spa.Options.StartupTimeout = TimeSpan.FromMinutes(5);
```

Both messages carry the underlying script's own stdout and stderr, so read past the timeout line before assuming the machine was merely slow.

## Static files: `AddSpaStaticFilesImproved`

`AddSpaStaticFilesImproved` registers the file provider that serves your SPA's built output. `SpaStaticFilesOptions` has a single member:

| Member | Type | Default | Meaning |
|---|---|---|---|
| `RootPath` | `string` | *(none — required)* | Path, relative to the application's content root, of the folder holding the built SPA files. |

`RootPath` is mandatory: leaving it empty throws `InvalidOperationException` when the provider is resolved. If the folder is configured but does not exist on disk, that is **not** an error — the provider simply supplies no files, and `UseSpaStaticFilesImproved()` serves nothing. That is exactly what you want in Development, where the assets live in the dev server rather than on disk.

Two ways to serve the files:

```csharp
app.UseSpaStaticFilesImproved();                 // default StaticFileOptions
app.UseSpaStaticFiles(new StaticFileOptions      // custom options
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000",
});
```

Calling either without having called `AddSpaStaticFilesImproved` throws `InvalidOperationException` telling you to register the provider first.

### Gotcha: Angular 17+ splits the output into `dist/browser` and `dist/server`

Up to Angular 16, `ng build` wrote `index.html` and the bundles straight into `ClientApp/dist` (or `dist/<project>`). From Angular 17 the application builder splits the output:

```
ClientApp/dist/
  browser/      <- index.html, main-*.js, styles-*.css   (what the browser downloads)
  server/       <- main.js                               (the SSR bundle Node runs)
```

`RootPath` must point at the **browser** folder:

```csharp
// Correct
services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/browser";
});

// Wrong - dist contains only the browser/ and server/ folders, no index.html
services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist";
});
```

With the wrong value, the folder exists, so the provider is happy and static-file middleware is wired up — but no request ever matches a file, and the default-page middleware cannot find `index.html` either. In Production the application therefore cannot serve its SPA **at all**, and every request ends in:

```
The SPA default page middleware could not return the default page '/index.html' because it was not
found, and no other middleware handled the request.
Your application is running in Production mode, so make sure it has been published, or that you have
built your SPA manually. ...
```

Development hides the mistake completely, because the dev-server proxy answers first and never touches `RootPath` — so this fails only after you deploy. If your SSR boot module is at `ClientApp/dist/server/main.js`, your `RootPath` is almost certainly `ClientApp/dist/browser`.

## Angular CLI dev server: `UseAngularCliServer`

```csharp
using System.Text.RegularExpressions;

if (env.IsDevelopment())
{
    spa.UseAngularCliServer(
        npmScript: "start",
        cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
}
```

What it does, in order:

1. Picks a port — `DevServerPort` if you set one, otherwise a free TCP port.
2. Runs `<PackageManagerCommand> run <npmScript> -- --port <port>` in `SourcePath` (`npmScript` defaults to `"start"`), tracking the child process so it is killed when the host exits.
3. Pipes the script's stdout to the logger at Information level and stderr at Error level, stripping ANSI colour codes, so the CLI's own output appears in your ASP.NET Core console.
4. Waits for its ready line (see below) and extracts the URL the CLI is listening on.
5. Polls that URL with `HEAD` requests until it answers anything at all — the CLI briefly rejects connections after announcing itself.
6. Registers the dev-server proxy for the rest of the pipeline.

**You do not run `ng serve` yourself.** The middleware owns the dev server's lifetime: it starts on the first request that needs it and is killed with the host. Running your own `ng serve` alongside just wastes a port. Equally, never enable this outside Development — in Production the built files under `RootPath` are what you want.

### How ready-detection works

The middleware watches the script's stdout for a regex and reads the URL out of a named group called `openbrowser`. The built-in default is:

```
open your browser on (?<openbrowser>http\S+)
```

which matches the classic Angular CLI banner. Newer CLI versions print something like `Local:   http://localhost:4200/` instead, so pass your own:

```csharp
spa.UseAngularCliServer(
    npmScript: "start",
    cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
```

Rules worth knowing:

- Every regex you pass is awaited **in sequence**, so an array lets you wait for several progress lines before declaring the server ready.
- At least one of them must contain the `openbrowser` group. If none does, startup fails with *"You assigned a custom value to SpaOptions.CliRegexes, but none of the regexes contains an `openbrowser` group."*
- The captured value is parsed as a `Uri` and becomes the proxy target — that is how a dynamically chosen port is discovered.
- If the script exits before any regex matches, you get an `InvalidOperationException` naming the package manager and script, with the captured stderr attached.

## The dev-server proxy: `UseProxyToSpaDevelopmentServer`

`UseAngularCliServer` uses this internally, but you can point it at an already-running dev server yourself:

```csharp
if (env.IsDevelopment())
{
    spa.UseProxyToSpaDevelopmentServer("http://localhost:4200");
}
```

Three overloads exist: `string baseUri`, `Uri baseUri`, and `Func<Task<Uri>> baseUriTaskFactory` — the last one for the case where the target is not known until the dev server has started.

Behaviour:

- **Everything is proxied.** It is registered with `IApplicationBuilder.Run`, so it terminates the pipeline: path and query string are forwarded verbatim, and even a 404 from the dev server is returned to the client instead of falling through (`proxy404s: true`).
- **WebSockets and HMR work.** `UseWebSockets()` is enabled for you, upgrade requests are re-issued to the dev server over `ws`/`wss` with the requested sub-protocols, and frames are pumped in both directions — which is what makes Angular's hot module replacement / live reload work through the ASP.NET Core host.
- **No request timeout.** The proxy's `HttpClient` uses an infinite timeout so server-sent events and other long-lived responses are not cut off. Startup is bounded by `StartupTimeout`; the proxied request itself is not.
- **Client disconnects are not errors.** Proxying is cancelled when either the client aborts or the host shuts down, and an `OperationCanceledException` or `IOException` from that is treated as *handled*, not failed. A user who navigates away mid-request produces no logged exception and no error page — which matters because a SPA does that constantly.
- **A dead target is a clear error.** If the dev server is not accepting requests, the `HttpRequestException` is rewrapped with the exact target URI and the advice to check that the target is running, with the original exception as `InnerException`.
- Hop-by-hop headers are dropped, and HTTP/1.1-only headers are stripped when the client speaks HTTP/2 or HTTP/3.

## The default page, and who serves `index.html`

After your callback returns, `UseSpaImproved` attaches the default-page middleware, which does three things:

1. Rewrites `context.Request.Path` to `Options.DefaultPage` — unless the request already matched an endpoint, in which case it is left alone. This is what makes a deep link like `/artists/42` return the SPA shell instead of a 404.
2. Serves that path as a static file, using `DefaultPageStaticFileOptions` if you supplied it, and falling back to `wwwroot` when no SPA static-file provider is registered.
3. If nothing served the file, throws `InvalidOperationException` with the diagnostic quoted in the `dist/browser` section above — including an extra hint when the environment is Production.

The genuinely confusing part is that **in Development this middleware is never reached.** `UseAngularCliServer` / `UseProxyToSpaDevelopmentServer` terminate the pipeline (`app.Run`, proxying 404s, never calling `next`), so every request that gets past MVC is answered by the dev server. `DefaultPage`, `DefaultPageStaticFileOptions` and `RootPath` have no effect at all while you are debugging.

| | Development | Production |
|---|---|---|
| Who serves `index.html` | The Angular CLI dev server, via the proxy | `SpaDefaultPageMiddleware`, from `RootPath` |
| Who serves JS/CSS assets | The dev server (in memory, freshly built) | Static-file middleware from `RootPath` |
| `UseSpaStaticFilesImproved()` | Typically skipped; serves nothing even if called, because `dist` does not exist | Required |
| `SpaOptions.DefaultPage` | Not used | Used |
| Deep links / client routing | Handled by the dev server | Handled by the default-page rewrite |
| HMR / live reload | Yes, proxied over WebSockets | No |
| Errors about a missing default page | Cannot occur | The symptom of a wrong `RootPath` |

This is why a misconfigured `RootPath` only ever shows up after deployment, and why "it works when I debug it" is not evidence that hosting is configured correctly. Run once with `ASPNETCORE_ENVIRONMENT=Production` against a published output before you ship.

## MSBuild integration

Referencing this package brings in [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices), which ships the props/targets that build your SPA as part of `dotnet publish`, restore `node_modules` on a Debug build, and add the built output to the publish set.

### Properties

| Property | Default | Description |
|---|---|---|
| `EnableSpaBuilder` | `true` | Master switch. Set to `false` in a project that references the package but has no SPA. |
| `SpaRoot` | `ClientApp\` | SPA source folder, relative to the project. Note the trailing separator. |
| `BuildServerSideRenderer` | `true` | When `true`, publish runs `npm run build:ssr:production`; when `false`, `npm run build -- --configuration production`. Set it to `true` only if your `package.json` really has a `build:ssr:production` script. |

```xml
<PropertyGroup>
  <SpaRoot>ClientApp\</SpaRoot>
  <!-- Set this to true if you enable server-side prerendering -->
  <BuildServerSideRenderer>true</BuildServerSideRenderer>
</PropertyGroup>
```

Disabling the SPA build entirely:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

### Targets

| Target | Runs | What it does |
|---|---|---|
| `DebugEnsureNodeEnv` | Before `Build`, Debug only, and only when `node_modules` is missing | Verifies `node --version` works (failing with a link to nodejs.org if not), then runs `npm install`. |
| `PublishRunWebpack` | After `ComputeFilesToPublish` | Builds the SPA for production and adds `dist` (plus `dist-server`, and `node_modules` when `BuildServerSideRenderer` is `true`) to the publish output. |
| `ComputeSpaFolderHash` / `EnsureHasherIgnoreFile` | Before the two above | Hashes `SpaRoot` so an unchanged SPA is not rebuilt, writing a default `.hasherignore` into `SpaRoot` on first use. |

While `EnableSpaBuilder` is `true`, the targets also keep `SpaRoot` out of `Compile`/`Content`/`None` and re-add it as non-publishing `None` items, so the SPA sources show up in the IDE without being copied to the output.

The full list of properties (including the build-caching and npm-workspace knobs) is documented with the [NodeServices package](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices).

## Complete example

```csharp
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllersWithViews();

        services.AddSpaStaticFilesImproved(configuration =>
        {
            configuration.RootPath = "ClientApp/dist/browser";
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();

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
            spa.Options.StartupTimeout = TimeSpan.FromMinutes(3);

            if (env.IsDevelopment())
            {
                spa.UseAngularCliServer(
                    npmScript: "start",
                    cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
            }
        });
    }
}
```

To add server-side rendering on top of this, keep everything above and add `spa.UseSpaPrerendering(...)` from [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) inside the same callback.

## Troubleshooting

### The dev server never starts

Symptoms: the first request hangs, then fails; or an exception saying the script *"exited without indicating that the Angular CLI was listening for requests"*, with the script's stderr attached.

- Read the attached stderr first — a broken `package.json`, a TypeScript error or a port clash all land here.
- Check that `spa.Options.SourcePath` points at the folder containing `package.json`. If it is empty, `UseAngularCliServer` throws immediately instead.
- Check that the script you named exists in `package.json` (`"start": "ng serve"` for the default `npmScript: "start"`).
- Run the same command by hand from `SourcePath` — `npm run start -- --port 4200` — to see whether the failure is yours or the middleware's.
- If the CLI clearly *is* running and printing its banner, but the middleware keeps waiting, it is the ready-regex that does not match. See the next-but-one item.

### Startup timeout

```
The Angular CLI process did not start listening for requests within the timeout period of 120 seconds.
```

The dev server did not become ready in time. On a cold CI machine or the first build after `npm install`, 120 seconds is often simply too short — raise `spa.Options.StartupTimeout`. The same property bounds the SSR bundle build, whose timeout message names `SpaOptions.StartupTimeout` explicitly and attaches the build's own output. Only after ruling out slowness should you suspect a regex mismatch or a build that never finishes.

### `The SPA default page middleware could not return the default page '/index.html'`

Production could not find `index.html`. In order of likelihood:

1. `RootPath` points at `ClientApp/dist` on Angular 17+, where `index.html` lives in `ClientApp/dist/browser`. Fix the path.
2. The SPA was never built — publish the application, or run the production build manually.
3. `EnableSpaBuilder` is `false`, or `SpaRoot` does not match your actual folder, so `dotnet publish` skipped the SPA build (it warns: *"SPA root folder ... does not exist"*).
4. `DefaultPage` was pointed at a file that is not in the published output.

### HMR / live reload does not connect

- WebSocket upgrades only survive if the request reaches the proxy. Make sure `UseSpaImproved` is registered **after** routing and MVC, and that nothing earlier short-circuits the dev server's socket path.
- Reverse proxies and tunnels in front of Kestrel must be configured to forward WebSocket upgrades.
- If the dev server rejects the upgrade, the proxy answers `400`; check the CLI's own output (`allowedHosts`, host-header checks) in your ASP.NET Core log, where it is forwarded.
- Angular falls back to long polling when the WebSocket handshake is slow — most visibly on Windows, where the client connect can take over a second. Reload still works; only the transport differs.

### `npm` not found

```
Failed to start 'npm'. To resolve this:.

[1] Ensure that 'npm' is installed and can be found in one of the PATH directories.
    Current PATH enviroment variable is: ...
[2] See the InnerException for further details of the cause.
```

The diagnostic prints the PATH actually seen by the host process, which is the point: an IDE or service launched before Node was installed keeps the old environment, so `npm` works in your terminal and not in the app. Restart the IDE (or the machine) after installing Node, or set `PackageManagerCommand` to the package manager you really use.

This message now reaches you intact. Previously the startup wait re-threw the fault wrapped in an `AggregateException`, so all you saw was *"One or more errors occurred."* and the PATH dump was buried — if you are looking at that older message, the real cause is in the inner exception.

### Everything works in Development and breaks in Production

That asymmetry is expected and is almost always the hosting configuration: in Development the dev-server proxy is terminal and serves everything, so `RootPath`, `DefaultPage` and the published `dist` are never exercised. Test a published build with `ASPNETCORE_ENVIRONMENT=Production` before deploying.

## Related Packages

- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration and the MSBuild targets (included)
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - `ISpaBuilder` / `ISpaOptions` abstractions (included)
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Server-side rendering on top of this package
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - Define your SPA routes in ASP.NET Core
- [MintPlayer.AspNetCore.SpaServices.Xsrf](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf) - Antiforgery token support for SPAs

## License

This project is licensed under the Apache 2.0 License.
