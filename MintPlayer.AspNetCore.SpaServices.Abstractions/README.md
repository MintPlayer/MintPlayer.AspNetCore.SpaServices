# MintPlayer.AspNetCore.SpaServices.Abstractions

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Abstractions.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Abstractions.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package is the contract layer of the MintPlayer SPA services family: three interfaces, no implementation, no third-party dependencies. It exists so a library can plug into SPA hosting and prerendering — reading the SPA's configuration, or supplying its own build step for a non-Angular toolchain — without taking a dependency on `MintPlayer.AspNetCore.SpaServices` itself. If you are building an application rather than a library, you already have these types transitively and can ignore this package.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Abstractions
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Abstractions
```

## What's in the box

Everything lives in the `MintPlayer.AspNetCore.SpaServices.Abstractions` namespace.

| Interface | One-line summary |
|-----------|------------------|
| `ISpaBuilder` | The handle passed to SPA configuration callbacks: the middleware pipeline plus the SPA's options. |
| `ISpaOptions` | The SPA's hosting configuration (default page, source path, dev-server port, package manager, startup timeout). |
| `ISpaPrerendererBuilder` | A development-time hook that builds the SPA's server bundle before prerendering runs. **The one interface you are expected to implement yourself.** |

### `ISpaBuilder`

```csharp
public interface ISpaBuilder
{
    IApplicationBuilder ApplicationBuilder { get; }
    ISpaOptions Options { get; }
}
```

**What it is for:** it's the object handed to the `UseSpaImproved(spa => ...)` configuration callback, and to everything that extends SPA hosting. `ApplicationBuilder` gives you the middleware pipeline the SPA is hosted in — and, through `ApplicationBuilder.ApplicationServices`, the application's service provider, which is how integration code resolves loggers, `IHostApplicationLifetime`, `IWebHostEnvironment` and so on. `Options` gives you the SPA's configuration.

**Who implements it:** `MintPlayer.AspNetCore.SpaServices` does, internally. You consume it; you don't implement it.

**When you'd reference this package for it:** to write extension methods on `ISpaBuilder` — the way `UseAngularCliServer`, `UseProxyToSpaDevelopmentServer` and `UseSpaPrerendering` are written — from a library that shouldn't depend on the implementation package.

```csharp
using MintPlayer.AspNetCore.SpaServices.Abstractions;

public static class MyToolchainSpaBuilderExtensions
{
    public static void UseMyDevServer(this ISpaBuilder spaBuilder, int port)
    {
        var sourcePath = spaBuilder.Options.SourcePath
            ?? throw new InvalidOperationException("Set SpaOptions.SourcePath first.");

        var logger = spaBuilder.ApplicationBuilder.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyToolchain");

        logger.LogInformation("Serving {SourcePath} via my dev server on port {Port}.", sourcePath, port);

        // ... attach your middleware to spaBuilder.ApplicationBuilder here
    }
}
```

### `ISpaOptions`

```csharp
public interface ISpaOptions
{
    PathString DefaultPage { get; set; }
    StaticFileOptions? DefaultPageStaticFileOptions { get; set; }
    string? SourcePath { get; set; }
    int DevServerPort { get; set; }
    string PackageManagerCommand { get; set; }
    TimeSpan StartupTimeout { get; set; }
}
```

| Member | Default in the implementation | Meaning |
|--------|-------------------------------|---------|
| `DefaultPage` | `/index.html` | URL of the page that hosts the SPA shell. Cannot be set to null/empty. |
| `DefaultPageStaticFileOptions` | `null` | Static-file options used to serve the default page. When null, files are read from the web root (`wwwroot`). |
| `SourcePath` | `null` | Path, relative to the application working directory, of the SPA sources at development time. May not exist in a published app. Most integration code needs this and should fail with a clear message when it is null or empty. |
| `DevServerPort` | `0` | Port for the SPA development server. `0` means pick a free port on every start; a fixed value pins it. |
| `PackageManagerCommand` | `npm` | Executable used to run SPA scripts (`npm`, `yarn`, `pnpm`, ...). Cannot be set to null/empty. Respect it instead of hard-coding `npm`. |
| `StartupTimeout` | 2 minutes | How long a request may wait for the SPA to become ready. Use it to bound your own waits rather than inventing a timeout. |

**What it is for:** the shared vocabulary between SPA hosting, the dev-server integrations and the prerenderer, so a builder or middleware can find the SPA's sources and toolchain without being told twice.

**Who implements it:** `MintPlayer.AspNetCore.SpaServices.Core.SpaOptions`. Note that the concrete type carries a few extra members not on this interface (such as the dev-server output regexes), so options set through `ISpaBuilder.Options` are limited to the members above — everything else has to be configured on the concrete type by the application.

**When you'd reference this package for it:** whenever you read SPA configuration. Bind against `ISpaOptions`, never the concrete class.

### `ISpaPrerendererBuilder`

```csharp
public interface ISpaPrerendererBuilder
{
    Task Build(ISpaBuilder spaBuilder);
}
```

**What it is for:** producing the JavaScript boot file that prerendering middleware executes in Node.js. It is a **development-time** facility: in production the server bundle is built during publish and no builder should run. The prerendering middleware awaits `Build` once, before it first looks for the boot module on disk, so a slow build delays the first request rather than every request.

**Who implements it:** `MintPlayer.AspNetCore.SpaServices.Prerendering.AngularPrerendererBuilder` ships for the Angular CLI. Everything else is yours to write — this is the extension point of the family.

**When you'd reference this package for it:** when your SPA is built by something other than the Angular CLI (Vite, webpack, Nuxt, esbuild, a Makefile, ...) and you want the same "just run the app and the server bundle appears" experience in development.

## Implementing `ISpaPrerendererBuilder` for another toolchain

A prerenderer builder is genuinely small: read the SPA's source path and package manager from the options, run the build script, and complete when it succeeds. Bound the wait with `StartupTimeout` and honour `ApplicationStopping` so a Ctrl+C during a slow build doesn't hang shutdown.

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Abstractions;

/// <summary>
/// Builds an SSR bundle by running a package.json script once, for any toolchain
/// whose build command exits when it is done (Vite, webpack, esbuild, ...).
/// Development-time only.
/// </summary>
public sealed class ScriptPrerendererBuilder : ISpaPrerendererBuilder
{
    private readonly string _scriptName;

    public ScriptPrerendererBuilder(string scriptName)
    {
        if (string.IsNullOrEmpty(scriptName))
        {
            throw new ArgumentException("Cannot be null or empty.", nameof(scriptName));
        }

        _scriptName = scriptName;
    }

    public async Task Build(ISpaBuilder spaBuilder)
    {
        var sourcePath = spaBuilder.Options.SourcePath;
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new InvalidOperationException(
                $"To use {nameof(ScriptPrerendererBuilder)}, set SpaOptions.SourcePath.");
        }

        var services = spaBuilder.ApplicationBuilder.ApplicationServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ScriptPrerendererBuilder>();
        var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        var packageManager = spaBuilder.Options.PackageManagerCommand; // 'npm' unless overridden
        var startInfo = new ProcessStartInfo(packageManager)
        {
            Arguments = $"run {_scriptName}",
            WorkingDirectory = Path.GetFullPath(sourcePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        logger.LogInformation("Building the SSR bundle: {Command} run {Script}", packageManager, _scriptName);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start '{packageManager}'. Make sure it is installed and on PATH.");

        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(stopping)
                .WaitAsync(spaBuilder.Options.StartupTimeout, stopping);
        }
        catch (TimeoutException ex)
        {
            TryKill(process);
            throw new InvalidOperationException(
                $"The {packageManager} script '{_scriptName}' did not finish within " +
                $"{spaBuilder.Options.StartupTimeout.TotalSeconds} seconds. Adjust " +
                "SpaOptions.StartupTimeout if the build legitimately takes longer.", ex);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The {packageManager} script '{_scriptName}' exited with code {process.ExitCode}.\n" +
                $"Output was: {await stdOut}\n" +
                $"Error output was: {await stdErr}");
        }

        logger.LogInformation("SSR bundle built.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Nothing useful to do if the process is already gone.
        }
    }
}
```

Plugging it in requires [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) in the **application** (your library only needs the abstractions):

```csharp
app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";

    if (env.IsDevelopment())
    {
        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
            // Development only: leave BootModuleBuilder null in production.
            options.BootModuleBuilder = new ScriptPrerendererBuilder("build:ssr");
        });
    }
});
```

Two things to keep in mind when writing your own:

- **A watch-mode build never exits.** The Angular builder that ships with the family runs its script with `--watch` and resolves when a success pattern appears in stdout a given number of times, so later rebuilds keep happening in the background. The example above assumes a one-shot build that exits; if you want watch behaviour, resolve on an output pattern instead of on process exit — and make sure you can still time out and cancel.
- **Always bound the wait.** A build script that neither matches nor exits will otherwise leave the first request hanging forever. That is what `ISpaOptions.StartupTimeout` is for.

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core implementation (`SpaOptions`, `UseSpaImproved`, dev-server integration)
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support and the Angular prerenderer builder
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration
- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js invocation and the MSBuild/npm build integration
- [MintPlayer.AspNetCore.SpaServices.Xsrf](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf) - CSRF token cookie for SPAs

## License

This project is licensed under the Apache 2.0 License.
