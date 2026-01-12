# MintPlayer.AspNetCore.SpaServices

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package contains the ASP.NET Core SPA prerenderer, enabling server-side rendering for single-page applications built with Angular, React, or other frameworks.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices
```

## Features

- Server-side prerendering for SPAs
- Integration with ASP.NET Core middleware pipeline
- Support for data passing between server and client

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

## Basic Usage

```csharp
app.UseSpa(spa =>
{
    spa.Options.SourcePath = "ClientApp";

    spa.UseSpaPrerendering(options =>
    {
        options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
        options.BootModuleBuilder = env.IsDevelopment()
            ? new AngularCliBuilder(npmScript: "build:ssr")
            : null;
        options.ExcludeUrls = new[] { "/sockjs-node" };
    });

    if (env.IsDevelopment())
    {
        spa.UseAngularCliServer(npmScript: "start");
    }
});
```

## Related Packages

- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration (included)
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Enhanced prerendering
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration

## License

This project is licensed under the Apache 2.0 License.
