# MintPlayer.AspNetCore.NodeServices

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package lets ASP.NET Core call into JavaScript that runs in a real Node.js process. It is a maintained fork of Microsoft's discontinued `Microsoft.AspNetCore.NodeServices` — its own package description calls it "the abandoned node services" — kept alive because the MintPlayer SPA prerendering packages need a working Node.js RPC channel. It also carries the MSBuild integration (`npm install`, SPA build, build caching) that the whole package family relies on, which is why this README is the reference for those build properties and targets.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.NodeServices
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.NodeServices
```

## Do you actually need this package?

Most people don't install it directly. If you use [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering), you already have this package transitively, and the prerendering middleware talks to Node.js for you — you never touch `INodeServices`.

Reference it directly only when you want to run your own JavaScript from C#: a JS-only library with no .NET equivalent, a Markdown/LaTeX/highlighting pipeline, an image or PDF tool, a custom SSR flow. Treat it the way you'd treat any out-of-process dependency: it launches and manages a child `node` process, JSON-serializes arguments over loopback HTTP, and JSON-deserializes the result.

## Registration

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNodeServices();

// ...or with options
builder.Services.AddNodeServices(options =>
{
    options.ProjectPath = builder.Environment.ContentRootPath;
    options.InvocationTimeoutMilliseconds = 30_000;
});

var app = builder.Build();
```

`AddNodeServices` registers `INodeServices` as a **singleton**. That is deliberate: the singleton owns the Node.js child process and replaces it when it dies or when a watched file changes, so the `INodeServices` reference you hold stays valid across restarts of the underlying process. Inject it anywhere:

```csharp
app.MapGet("/greet/{name}", async (string name, INodeServices nodeServices, CancellationToken ct) =>
{
    var greeting = await nodeServices.InvokeAsync<string>(ct, "./Node/greeter", name);
    return Results.Text(greeting);
});
```

You can also create an instance outside DI with `NodeServicesFactory.CreateNodeServices(new NodeServicesOptions(serviceProvider))`. It implements `IDisposable`; disposing it kills the Node.js process.

## The `INodeServices` API

`INodeServices` has exactly four members (plus `Dispose`), which are two methods in two overloads each.

| Member | Purpose |
|--------|---------|
| `Task<T> InvokeAsync<T>(string moduleName, params object[] args)` | Invokes the module's default export. **No cancellation token** — runs with `CancellationToken.None`. |
| `Task<T> InvokeAsync<T>(CancellationToken cancellationToken, string moduleName, params object[] args)` | Same, but cancellable. Prefer this one. |
| `Task<T> InvokeExportAsync<T>(string moduleName, string exportedFunctionName, params object[] args)` | Invokes a **named** export. **No cancellation token.** |
| `Task<T> InvokeExportAsync<T>(CancellationToken cancellationToken, string moduleName, string exportedFunctionName, params object[] args)` | Same, but cancellable. Prefer this one. |

`moduleName` is the path to a JavaScript file, resolved relative to `NodeServicesOptions.ProjectPath` (which defaults to `IWebHostEnvironment.ContentRootPath`). The `.js` extension is optional. `args` must be JSON-serializable; they are serialized with camel-cased property names.

`T` may be any JSON-deserializable type, `string` (a `text/plain` response from Node.js bypasses JSON entirely), or `System.IO.Stream` (for a streamed `application/octet-stream` response).

### ⚠ The overload trap

Both overloads of each method end in `params object[] args`. That means **forgetting the `CancellationToken` compiles silently**: the token-less overload accepts any argument list, so the call builds cleanly and runs with `CancellationToken.None`. Nothing warns you. Overload resolution keys entirely off whether a `CancellationToken` sits in the *first* position:

```csharp
// Cancellable: token is the FIRST argument, so the CancellationToken overload is chosen.
await nodeServices.InvokeAsync<string>(ct, "./Node/render", model);

// NOT cancellable: the token-less overload is chosen and `ct` is JSON-serialized as an argument!
await nodeServices.InvokeAsync<string>("./Node/render", ct, model);

// NOT cancellable: runs with CancellationToken.None. Compiles fine. No warning.
await nodeServices.InvokeAsync<string>("./Node/render", model);
```

For anything driven by an HTTP request, always pass `HttpContext.RequestAborted` (or an injected `CancellationToken`) as the first argument. Without it, a client that disconnects leaves your invocation waiting until the invocation timeout expires.

### Important caveat: cancellation does not stop Node.js

Cancelling frees the **.NET** side only. The RPC protocol has no abort channel: the HTTP request to the Node.js process is abandoned, your `Task` faults with an `OperationCanceledException`, and the JavaScript function keeps running to completion inside the Node.js process. Its CPU, memory and side effects (files written, requests sent) all still happen; only the result is discarded.

Practical consequences:

- Cancellation is a way to stop *waiting*, not a way to shed load. A burst of cancelled requests still costs you the full Node.js work.
- Make the JavaScript side idempotent, or cheap enough that an abandoned run doesn't matter.
- If a runaway script must be stopped, the only real lever is process replacement (see below) or a shorter `InvocationTimeoutMilliseconds`.

## Writing the JavaScript side

The invoked function receives a **callback as its first parameter**, followed by the arguments you passed from C#. The callback is Node.js-style: `callback(error, result)`.

```javascript
// Node/greeter.js  — default export
module.exports = function (callback, name) {
    try {
        callback(/* error */ null, `Hello, ${name}!`);
    } catch (err) {
        callback(err, null);
    }
};
```

Called from C# as:

```csharp
var greeting = await nodeServices.InvokeAsync<string>(ct, "./Node/greeter", "Alice");
```

Named exports work the same way and are invoked with `InvokeExportAsync`:

```javascript
// Node/text-tools.js
exports.toUpper = function (callback, text) {
    callback(null, text.toUpperCase());
};

exports.wordCount = function (callback, text) {
    callback(null, { words: text.split(/\s+/).filter(Boolean).length });
};
```

```csharp
public sealed record WordCount(int Words);

var upper = await nodeServices.InvokeExportAsync<string>(
    ct, "./Node/text-tools", "toUpper", "hello world");

var count = await nodeServices.InvokeExportAsync<WordCount>(
    ct, "./Node/text-tools", "wordCount", "hello world");
```

Both CommonJS (`module.exports = ...`, `exports.name = ...`) and ES-module (`export default`, `export function`) shapes are supported; a default export that is a function is used when no export name is given.

### Your function MUST always invoke the callback

**This is the single most common source of trouble.** The .NET side has no other signal that the work is done. A function that returns without calling `callback` — because it took an early `return`, swallowed an exception, or awaited a promise that never settles — produces nothing at all until the invocation timeout fires, and then surfaces as a timeout error rather than as your actual bug. Returning a `Promise` is not enough either: the result is read from the callback.

```javascript
// WRONG — the error path never calls back, so failures appear as timeouts
module.exports = function (callback, id) {
    doWork(id, (err, result) => {
        if (err) { return; }          // <-- .NET now waits for the full timeout
        callback(null, result);
    });
};

// RIGHT — every path calls back exactly once
module.exports = function (callback, id) {
    doWork(id, (err, result) => {
        if (err) { callback(err, null); }
        else { callback(null, result); }
    });
};

// RIGHT — async/await bridged to the callback
module.exports = async function (callback, id) {
    try {
        callback(null, await doWorkAsync(id));
    } catch (err) {
        callback(err, null);
    }
};
```

Throwing synchronously is also fine — the host catches it and turns it into a .NET exception. It's the silent no-callback path that hurts.

To stream a large response instead of buffering it, write to `callback.stream` (a Node.js `http.ServerResponse`) and request `Stream` (or `object`) as `T` on the .NET side.

`console.log`/`console.error` from your module are redirected to `NodeServicesOptions.NodeInstanceOutputLogger`, so they appear in your ASP.NET Core logs under the `MintPlayer.AspNetCore.NodeServices` category.

## `NodeServicesOptions`

Configure via the `AddNodeServices(options => ...)` callback. Defaults marked "from DI" are read from the service provider when the options object is constructed.

| Member | Type | Default | Meaning |
|--------|------|---------|---------|
| `ProjectPath` | `string` | `IWebHostEnvironment.ContentRootPath` if available, otherwise `Directory.GetCurrentDirectory()` (from DI) | Root used to resolve `moduleName` paths. Also becomes the Node.js process's working directory, and `<ProjectPath>/node_modules` is appended to `NODE_PATH` so `require` finds your packages. |
| `NodePath` | `string` | `"node"` | The Node.js executable. Left as `"node"`, it is resolved through `PATH`; set an absolute path to pin a specific install. |
| `InvocationTimeoutMilliseconds` | `int` | `60000` (60s) | Maximum time .NET waits for one RPC call. `0` or less disables the timeout. The internal `HttpClient` timeout is this value plus one second. |
| `NodeInstanceOutputLogger` | `ILogger` | Logger for category `MintPlayer.AspNetCore.NodeServices` if an `ILoggerFactory` is registered, else `NullLogger.Instance` (from DI) | Receives the Node.js process's stdout (as information) and stderr (as errors). |
| `WatchFileExtensions` | `string[]` | `[".js", ".jsx", ".ts", ".tsx", ".json", ".html"]` | Extensions watched recursively under `ProjectPath`. A change flags the Node.js instance for restart. Set to `null` or an empty array to disable file watching. |
| `EnvironmentVariables` | `IDictionary<string, string>` | `{ "NODE_ENV": "development" \| "production" }` when an `IWebHostEnvironment` is available, otherwise empty (from DI) | Environment variables for the Node.js child process. |
| `ApplicationStoppingToken` | `CancellationToken` | `IHostApplicationLifetime.ApplicationStopping` if available (from DI) | Used to clean up the temporary entry-point script on shutdown. |
| `LaunchWithDebugging` | `bool` | `false` | Starts Node.js with `--inspect` so a V8 debugger can attach. While true, a replaced instance is killed immediately instead of draining, because it would otherwise hold the debugging port. |
| `DebuggingPort` | `int` | `0` | With `LaunchWithDebugging`, the port passed as `--inspect=<port>`. `0` means let Node.js choose. |
| `NodeInstanceFactory` | `Func<INodeInstance>` | HTTP hosting (`options.UseHttpHosting()`, applied in the constructor) | How Node.js instances are created. The only built-in hosting model is out-of-process-over-loopback-HTTP; override only if you implement your own `INodeInstance`. |

`NodeServicesOptionsExtensions.UseHttpHosting(this NodeServicesOptions)` re-applies the default hosting model. You rarely need to call it — the constructor already does.

## Failures, restarts and connection draining

### What you will actually catch

| Situation | What you get |
|-----------|--------------|
| JavaScript calls `callback(err, null)` or throws | An exception whose `Message` carries the JS error message and whose text includes the Node.js stack trace as detail. |
| The invocation exceeds `InvocationTimeoutMilliseconds` | An exception saying `The Node invocation timed out after <n>ms.`, with guidance to check that your function always invokes its callback. **This is a timeout, not a cancellation** — it is not an `OperationCanceledException`. |
| *You* cancelled (client disconnected, shutdown, your own token) | `OperationCanceledException` / `TaskCanceledException`. The Node.js work continues regardless. |
| Node.js could not be started at all | `InvalidOperationException` whose message lists the current `PATH` and how to fix it, with the underlying failure as `InnerException`. |
| Node.js responded with a string but `T` isn't `string` (or binary but `T` isn't `Stream`) | `ArgumentException` naming the requested type. |

Internally the failing-invocation cases are represented by a `NodeInvocationException` type. **That type is `internal`**, so you cannot `catch (NodeInvocationException)` from your own code — catch `Exception` (or let it bubble to your error handling) and read `Message`. Do distinguish cancellation, though, which *is* a public framework type:

```csharp
try
{
    return await nodeServices.InvokeAsync<string>(ct, "./Node/render", model);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // The caller went away. Nothing to report; Node.js finishes the work anyway.
    throw;
}
catch (Exception ex)
{
    // Node.js threw, or the invocation timed out. ex.Message has the JS message/stack.
    logger.LogError(ex, "Prerendering failed");
    throw;
}
```

### Automatic retry once, then restart

When an invocation fails because the Node.js instance is unusable — the process has exited, or a watched file changed and it has been flagged for restart — the failure is marked "instance unavailable" and handled transparently:

1. The current instance is detached and scheduled for disposal.
2. A brand-new Node.js process is launched.
3. The invocation is retried **once** against the new instance.

The retry is deliberately not repeated: a freshly launched instance that still cannot accept invocations indicates a real problem, and retrying in a loop would only mask it. So a genuine failure surfaces after exactly two attempts.

### The 15-second draining window

The replaced instance is not killed immediately. It is disposed after a fixed **15-second connection-draining window**, so invocations that were already in flight against it get a chance to finish — the same idea as connection draining in an HTTP load balancer. Two things follow from this:

- Immediately after a file change you can briefly have **two** Node.js processes alive. This is expected, and the old one goes away within 15 seconds.
- The window is skipped when `LaunchWithDebugging` is true, because the old process would otherwise keep the debugging port and prevent the new one from starting.

If the delayed disposal itself fails, the error is captured and rethrown to the *next* caller wrapped in an `AggregateException` (nothing else is waiting on that background task, so it would otherwise be swallowed).

## MSBuild Integration

This package ships MSBuild props and targets (`nodeservices.props`, `nodeservices.targets`, `npm-install.proj`) in `buildTransitive`, so they apply to your project whether you reference the package **directly or transitively** — including via the SpaServices and Prerendering packages. This is the authoritative documentation for them.

### Properties

Override any of these in your `.csproj` (or in a `Directory.Build.props`):

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSpaBuilder` | `true` | Master switch for all SPA build automation in this package. |
| `SpaRoot` | `ClientApp\` | Path to your SPA source folder, relative to the project. Keep the trailing slash. |
| `BuildServerSideRenderer` | `true` | Whether the publish build produces the SSR bundle (selects the npm script, and publishes `node_modules`). |
| `EnableSpaBuildCaching` | `true` | Enable folder-hash-based skipping of the SPA build. |
| `SpaHashFilePath` | `$(IntermediateOutputPath)spa-folder.hash` | Where the previous folder hash is stored (under `obj/` by default). |
| `ForceSpaBuild` | `false` | Build even when the hash says nothing changed. |
| `CreateDefaultHasherIgnore` | `true` | Create a default `.hasherignore` in `$(SpaRoot)` if none exists. |
| `NpmInstallWorkingDirectory` | `$(SpaRoot)` | Directory `npm install` runs in. Point this at your workspace root when using npm workspaces. |
| `NodeModulesCheckPath` | `$(NpmInstallWorkingDirectory)` | Directory checked for an existing `node_modules` folder. |

### Targets

| Target | When it runs | What it does |
|--------|--------------|--------------|
| `EnsureHasherIgnoreFile` | Before `ComputeSpaFolderHash` | Writes a default `$(SpaRoot).hasherignore` if it's missing and `CreateDefaultHasherIgnore` is `true`. |
| `ComputeSpaFolderHash` | Before `DebugEnsureNodeEnv` and `PublishRunWebpack` | Hashes `$(SpaRoot)` (honouring `.hasherignore`), compares with the stored hash, and sets `SpaSourceChanged`. Logs which of "unchanged", "changed" or "first build" it decided. |
| `DebugEnsureNodeEnv` | `BeforeTargets="Build"`, **`Debug` only**, and only when `$(NodeModulesCheckPath)node_modules` does not exist | Runs `node --version` and fails with an install-Node.js message if that doesn't succeed, then delegates to `npm-install.proj`. |
| `PublishRunWebpack` | `AfterTargets="ComputeFilesToPublish"` | Restores packages if needed, runs the production SPA build unless the cache says it can be skipped, stores the new hash, and adds the build output to the publish set. |

`npm-install.proj` is a tiny helper project invoked via the `<MSBuild>` task; it runs `npm install` in `$(NpmInstallWorkingDirectory)` only when `$(NodeModulesCheckPath)node_modules` is absent.

### What gets published

`PublishRunWebpack` adds `$(SpaRoot)dist\**` and `$(SpaRoot)dist-server\**` to `ResolvedFileToPublish` with `CopyToPublishDirectory=PreserveNewest`. When `BuildServerSideRenderer` is `true` it also publishes `$(SpaRoot)node_modules\**`, because the server-side bundle needs them at runtime. It warns (rather than failing) if `$(SpaRoot)` does not exist.

### File exclusions

`nodeservices.props` also keeps your SPA sources out of the .NET build while leaving them visible in Solution Explorer: `$(SpaRoot)**` is removed from `Compile`, `Content`, `EmbeddedResource` and `None`, then re-added to `None` with `$(SpaRoot)node_modules\**` excluded.

### Disabling SPA builder

If your project picks this package up transitively but has no SPA of its own:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

This skips `DebugEnsureNodeEnv`, `PublishRunWebpack`, the hashing targets and the SPA file-exclusion rules.

### Customizing the SPA root

```xml
<PropertyGroup>
  <SpaRoot>src\frontend\</SpaRoot>
</PropertyGroup>
```

### Client-only builds

```xml
<PropertyGroup>
  <BuildServerSideRenderer>false</BuildServerSideRenderer>
</PropertyGroup>
```

### Build command selection

| `BuildServerSideRenderer` | Command run in `$(SpaRoot)` during publish |
|---------------------------|--------------------------------------------|
| `true` (default) | `npm run build:ssr:production` |
| `false` | `npm run build -- --configuration production` |

Your `package.json` must define whichever script applies.

## SPA Build Caching

By default the SPA build is skipped when nothing relevant changed:

1. Before build and publish, a hash of `$(SpaRoot)` is computed, honouring `.hasherignore`.
2. It is compared against the hash stored at `$(SpaHashFilePath)` (`obj/spa-folder.hash`).
3. If the hash is unchanged **and** `$(SpaRoot)dist` exists, the npm build is skipped and the existing output is published.
4. Otherwise the npm build runs, and the new hash is stored afterwards.

The default is fail-safe: if anything is unknown (no stored hash, no `dist` folder), the build runs.

### Default ignore patterns

A `.hasherignore` is created in `$(SpaRoot)` on first use, containing:

| Category | Patterns |
|----------|----------|
| Dependencies | `node_modules/` |
| Build outputs | `dist/`, `dist-server/`, `build/`, `out/` |
| Framework caches | `.angular/`, `.cache/`, `.npm/` |
| Test outputs | `coverage/`, `test-results/`, `.nyc_output/` |
| IDE files | `.idea/`, `.vscode/` |
| Editor temp files | `*.swp`, `*.swo`, `*~` |
| OS files | `.DS_Store`, `Thumbs.db` |

This keeps huge folders like `node_modules` and transient files from slowing hashing down or causing pointless rebuilds.

### Customizing `.hasherignore`

The syntax is `.gitignore`-like:

```
# Build outputs (don't trigger rebuild when these change)
dist/
dist-server/
.angular/

# Dependencies
node_modules/

# IDE files
.idea/
.vscode/

# Test artifacts
coverage/
```

To stop the file from being created automatically:

```xml
<PropertyGroup>
  <CreateDefaultHasherIgnore>false</CreateDefaultHasherIgnore>
</PropertyGroup>
```

To add to the defaults *before* the file is generated:

```xml
<ItemGroup>
  <SpaHashDefaultIgnorePattern Include="my-custom-folder/" />
</ItemGroup>
```

Note that once `.hasherignore` exists it is never rewritten, so `SpaHashDefaultIgnorePattern` has no effect on an existing file — edit the file itself.

### Disabling caching / forcing a rebuild

```xml
<PropertyGroup>
  <!-- always rebuild -->
  <EnableSpaBuildCaching>false</EnableSpaBuildCaching>
  <!-- or: keep caching, but rebuild this time -->
  <ForceSpaBuild>true</ForceSpaBuild>
</PropertyGroup>
```

```bash
dotnet publish -p:ForceSpaBuild=true
```

## npm Workspaces Support

With [npm workspaces](https://docs.npmjs.com/cli/using-npm/workspaces), dependencies are hoisted into a single root `node_modules/`, so individual SPA folders such as `ClientApp/` no longer have one of their own — and the default `node_modules` check would reinstall on every build.

Point `NpmInstallWorkingDirectory` at the workspace root, ideally from a `Directory.Build.props` at your repository root:

```xml
<Project>
  <PropertyGroup>
    <NpmInstallWorkingDirectory>$(MSBuildThisFileDirectory)</NpmInstallWorkingDirectory>
  </PropertyGroup>
</Project>
```

`$(MSBuildThisFileDirectory)` resolves to the folder containing that file. This ensures:

- `npm install` runs where the `workspaces` field in `package.json` is defined.
- The `node_modules` check (`NodeModulesCheckPath`, which follows `NpmInstallWorkingDirectory` unless set separately) looks at the root, so every project skips the target once the install has happened.
- In a parallel solution build, `npm install` runs exactly once, because MSBuild deduplicates `<MSBuild>` calls to `npm-install.proj` made with identical properties.

## Troubleshooting

### `npm` not found during build or at development time

The build targets shell out to `npm`, and — if you also use [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) — so does the dev-server integration at runtime. When it can't be found, the diagnostic names the missing command and prints the **current `PATH`** so you can see what the build process actually inherited:

```
Failed to start 'npm'. To resolve this:.

[1] Ensure that 'npm' is installed and can be found in one of the PATH directories.
    Current PATH enviroment variable is: ...
    Make sure the executable is in one of those directories, or update your PATH.
```

Earlier versions lost this text: the underlying failure was wrapped in an `AggregateException` and reached the caller as the useless `One or more errors occurred.`. The fault is now propagated unwrapped, so the real message and `PATH` survive.

Usual causes: Node.js/npm installed after the IDE started (restart the IDE, not just the terminal — it caches the environment it was launched with), a build agent whose `PATH` differs from your shell's, or a Node.js version manager whose shims are only added by an interactive shell profile.

### `node` not on `PATH`

Launching the Node.js process throws an `InvalidOperationException` that likewise prints the current `PATH` and keeps the original error as `InnerException`. `DebugEnsureNodeEnv` catches the same problem earlier during `Debug` builds and fails with a "please install Node.js from https://nodejs.org/" message.

If Node.js exists but isn't on `PATH` (or you need a specific version), pin it:

```csharp
builder.Services.AddNodeServices(options =>
{
    options.NodePath = @"C:\Program Files\nodejs\node.exe";
});
```

### `The Node invocation timed out after 60000ms.`

In order of likelihood:

1. **Your JavaScript function never invoked its callback** (see "Your function MUST always invoke the callback" above). This is by far the most common cause, and the timeout is a symptom, not the bug.
2. The work genuinely takes longer than the timeout. Raise it:
   ```csharp
   builder.Services.AddNodeServices(options => options.InvocationTimeoutMilliseconds = 120_000);
   ```
3. `require` failed inside the module, or the module path is wrong. Check your logs — the Node.js process's stderr is forwarded to `NodeInstanceOutputLogger`. Remember `moduleName` resolves against `ProjectPath`, and `node_modules` is looked up under `$(ProjectPath)/node_modules`.

A related, rarer message — `Attempt to connect to Node timed out after <n>ms.` — means the process never reached the ready handshake at all, so look at startup errors in the log rather than at your function.

### Node.js processes surviving host shutdown

Normally there are two safety nets. On the .NET side, disposing `INodeServices` (or the instance being replaced) kills the child process. On the Node.js side, the child is launched with the host's PID and polls it, exiting on its own once the parent is gone — which covers a forceful kill of the .NET process, where no .NET cleanup can run.

If you still see stray `node` processes:

- Expect a brief overlap after a watched file changes: the replaced instance lives for up to 15 more seconds while draining.
- Debugger-attached processes can outlive a stop in the IDE; a process holding the `--inspect` port also blocks the replacement from starting, so kill it before restarting.
- A build-spawned `npm`/`ng` process is a different thing from the NodeServices instance. Those are owned by the SpaServices dev-server integration, which kills the whole process tree on shutdown.

### Node.js restarts constantly

The default `WatchFileExtensions` watches `.js`, `.jsx`, `.ts`, `.tsx`, `.json` and `.html` recursively under `ProjectPath`. If a build, a log file or a generated artifact under that root keeps touching such files, you get restart churn. Narrow the extension list, move the generated output outside `ProjectPath`, or disable watching:

```csharp
builder.Services.AddNodeServices(options => options.WatchFileExtensions = []);
```

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - Interfaces for integrating without the implementation
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support (the main consumer of this package)
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration
- [MintPlayer.AspNetCore.SpaServices.Xsrf](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf) - CSRF token cookie for SPAs

## License

This project is licensed under the Apache 2.0 License.
