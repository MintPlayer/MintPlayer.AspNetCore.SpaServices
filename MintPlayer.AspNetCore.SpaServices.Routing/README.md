# MintPlayer.AspNetCore.SpaServices.Routing

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package simplifies SPA prerendering by allowing you to define your SPA routes in ASP.NET Core and determine which route is activated in the `SupplyData` callback.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Routing
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Routing
```

## MSBuild Integration

This package includes `MintPlayer.AspNetCore.NodeServices` which automatically configures your project with build targets for SPA development.

### Properties

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSpaBuilder` | `true` | Master switch to disable SPA build automation |
| `SpaRoot` | `ClientApp\` | Path to your SPA source folder |
| `BuildServerSideRenderer` | `true` | Whether to build the SSR bundle |

### Disabling SPA Builder

If your project doesn't have a SPA but references this package:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

## Usage

### 1. Register SPA Routes

Define your SPA routes in `ConfigureServices`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSpaRoutes(routes => routes
        .Route("", "home")
        .Group("person", "person", person_routes => person_routes
            .Route("", "list")
            .Route("create", "create")
            .Route("{id}", "show")
            .Route("{id}/edit", "edit")
        )
    );
}
```

### 2. Supply Data Based on Route

Use `ISpaRouteService` to determine the current route and supply appropriate data:

```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ISpaRouteService spaRouteService)
{
    app.UseSpa(spa =>
    {
        spa.UseSpaPrerendering(options =>
        {
            options.SupplyData = (context, data) =>
            {
                var route = spaRouteService.GetCurrentRoute(context);
                var personRepository = context.RequestServices.GetRequiredService<IPersonRepository>();

                switch (route?.Name)
                {
                    case "person-list":
                        data["people"] = personRepository.GetPeople();
                        break;
                    case "person-show":
                    case "person-edit":
                        var id = Convert.ToInt32(route.Parameters["id"]);
                        data["person"] = personRepository.GetPerson(id);
                        break;
                }
            };
        });
    });
}
```

### 3. Use Data in Angular (main.server.ts)

```typescript
const providers: StaticProvider[] = [
    { provide: APP_BASE_HREF, useValue: params.baseUrl },
    { provide: 'BASE_URL', useValue: params.origin + params.baseUrl },
];

if ('people' in params.data) {
    providers.push({ provide: 'PEOPLE', useValue: params.data.people });
}
if ('person' in params.data) {
    providers.push({ provide: 'PERSON', useValue: params.data.person });
}
```

### 4. Generate URLs Server-Side

Generate SPA URLs from C# code (useful for redirects, sitemaps, etc.):

```csharp
// Using a dictionary
var parms = new Dictionary<string, object> { ["id"] = 5 };
var url = spaRouteService.GenerateUrl("person-edit", parms);

// Using an anonymous type
var url = spaRouteService.GenerateUrl("person-edit", new { id = 5 });
```

## Related Packages

- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration
- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support

## License

This project is licensed under the Apache 2.0 License.
