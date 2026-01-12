# MintPlayer.AspNetCore.SpaServices.Prerendering

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package provides enhanced server-side prerendering capabilities for ASP.NET Core single-page applications. It is a dependency of `MintPlayer.AspNetCore.SpaServices.Routing`.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Prerendering
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Prerendering
```

## Features

- Server-side prerendering with data injection
- Support for Angular Universal and similar SSR solutions
- Integration with ASP.NET Core request pipeline

## MSBuild Integration

This package includes `MintPlayer.AspNetCore.NodeServices` which automatically configures your project with build targets for SPA development.

See the [NodeServices documentation](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) for details on:
- `EnableSpaBuilder` - Master switch to disable SPA build automation
- `SpaRoot` - Configure your SPA source folder
- `BuildServerSideRenderer` - Toggle SSR bundle building

### Disabling SPA Builder

If your project doesn't have a SPA but references this package:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

## Usage

```csharp
spa.UseSpaPrerendering(options =>
{
    options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
    options.BootModuleBuilder = env.IsDevelopment()
        ? new AngularCliBuilder(npmScript: "build:ssr")
        : null;
    options.ExcludeUrls = new[] { "/sockjs-node" };

    options.SupplyData = (context, data) =>
    {
        // Supply data to the prerenderer
        data["message"] = "Hello from server!";
    };
});
```

## Related Packages

- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration
- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration (recommended)

## License

This project is licensed under the Apache 2.0 License.
