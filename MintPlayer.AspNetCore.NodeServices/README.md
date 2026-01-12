# MintPlayer.AspNetCore.NodeServices

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package provides Node.js integration for ASP.NET Core applications, enabling server-side execution of JavaScript code. It is a core dependency for SPA prerendering functionality.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.NodeServices
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.NodeServices
```

## MSBuild Integration

This package automatically configures your project with MSBuild props and targets for SPA development. These are applied transitively to all projects that reference this package.

### Properties

The following MSBuild properties are automatically configured (can be overridden in your `.csproj`):

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSpaBuilder` | `true` | Master switch to enable/disable all SPA build automation |
| `SpaRoot` | `ClientApp\` | Path to your SPA source folder |
| `BuildServerSideRenderer` | `true` | Whether to build the SSR bundle during publish |

### Build Targets

| Target | Runs | Description |
|--------|------|-------------|
| `DebugEnsureNodeEnv` | Before Build (Debug only) | Ensures Node.js is installed and runs `npm install` if `node_modules` doesn't exist |
| `PublishRunWebpack` | After ComputeFilesToPublish | Builds the SPA and includes output in publish folder |

### File Exclusions

The package automatically configures your project to:
- Exclude SPA source files from compilation
- Exclude `node_modules` from the project
- Show SPA files in Solution Explorer without including them in build output

## Disabling SPA Builder

If your project references this package (directly or transitively) but doesn't have a SPA, you can disable all build automation:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

This will:
- Skip the `DebugEnsureNodeEnv` target
- Skip the `PublishRunWebpack` target
- Skip SPA file exclusion rules

## Customizing SPA Root

If your SPA is in a different folder than `ClientApp`, override the `SpaRoot` property:

```xml
<PropertyGroup>
  <SpaRoot>src\frontend\</SpaRoot>
</PropertyGroup>
```

## Client-Only Builds

To build only the client bundle (without SSR) during publish:

```xml
<PropertyGroup>
  <BuildServerSideRenderer>false</BuildServerSideRenderer>
</PropertyGroup>
```

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration

## License

This project is licensed under the Apache 2.0 License.
