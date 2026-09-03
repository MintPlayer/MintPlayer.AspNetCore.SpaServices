# MintPlayer.AspNetCore.SpaServices

Server-side rendering for an Angular (or similar) single-page application hosted by ASP.NET Core.

These packages replace Microsoft's discontinued `Microsoft.AspNetCore.SpaServices` and
`Microsoft.AspNetCore.NodeServices`. ASP.NET Core hosts the SPA, starts the Angular CLI dev server
for you in development, and renders each page in Node.js on the way out — so crawlers and first
paint get a populated page instead of an empty shell. Your server code can push request-scoped data
(the current user, an entity from the database) straight into that render.

## Version info

| License                                                                                                               | Build status                                                                                                          | Code coverage | Code quality |
|-----------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------|---------------|--------------|
| [![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0) | ![.NET Core](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices.Routing/workflows/.NET%20Core/badge.svg) | [![Coverage](https://coverage.mintplayer.com/badge/MintPlayer/MintPlayer.AspNetCore.SpaServices.svg)](https://coverage.mintplayer.com/r/MintPlayer/MintPlayer.AspNetCore.SpaServices) | [![Codacy Badge](https://app.codacy.com/project/badge/Grade/a1528e2873ac4375881f4ccc00b70a91)](https://www.codacy.com/gh/MintPlayer/MintPlayer.AspNetCore.SpaServices.Routing?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=MintPlayer/MintPlayer.AspNetCore.SpaServices.Routing&amp;utm_campaign=Badge_Grade) |

| Package                                        | Release                                                                                                                                                                                         | Preview                                                                                                                                                                                            | Downloads |
|------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------|
| MintPlayer.AspNetCore.NodeServices             | [![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)                         | [![NuGet Version](https://img.shields.io/nuget/vpre/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)                         | [![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.NodeServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices)                         |
| MintPlayer.AspNetCore.SpaServices              | [![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)                           | [![NuGet Version](https://img.shields.io/nuget/vpre/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)                           | [![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices)                           |
| MintPlayer.AspNetCore.SpaServices.Prerendering | [![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) | [![NuGet Version](https://img.shields.io/nuget/vpre/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) | [![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Prerendering.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) |
| MintPlayer.AspNetCore.SpaServices.Routing      | [![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)           | [![NuGet Version](https://img.shields.io/nuget/vpre/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)           |[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)            |


## Which package do I install?

**Install [`MintPlayer.AspNetCore.SpaServices.Routing`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing).**
It pulls in the rest, and it is the only one that gives you route matching — knowing *which* page
is being rendered is what lets you supply the right data for it.

```
dotnet add package MintPlayer.AspNetCore.SpaServices.Routing
```

Each package has its own detailed documentation. Start with Routing; the others are reference.

| Package | What it is for | Documentation |
|---|---|---|
| [`…SpaServices.Routing`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) | **Start here.** Declares your SPA's client-side routes on the server, matches incoming URLs against them, generates URLs, and redirects. | [README](./MintPlayer.AspNetCore.SpaServices.Routing/README.md) |
| [`…SpaServices.Prerendering`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) | The prerendering middleware: captures the page your pipeline would have served, renders it in Node, supplies data to the render. | [README](./MintPlayer.AspNetCore.SpaServices.Prerendering/README.md) |
| [`…SpaServices`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) | Hosting the SPA: static files, the Angular CLI dev server, the dev-server proxy, the default page. | [README](./MintPlayer.AspNetCore.SpaServices/README.md) |
| [`…NodeServices`](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) | Invoking Node.js from .NET, and the MSBuild integration that all of these inherit. | [README](./MintPlayer.AspNetCore.NodeServices/README.md) |
| [`…SpaServices.Xsrf`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf) | CSRF tokens for a SPA — independent of the rest; install it only if you want it. | [README](./MintPlayer.AspNetCore.SpaServices.Xsrf/README.md) |
| [`…SpaServices.Abstractions`](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) | Interfaces only, for libraries that integrate without depending on the implementation. | [README](./MintPlayer.AspNetCore.SpaServices.Abstractions/README.md) |

## A minimal setup

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSpaStaticFilesImproved(configuration =>
    {
        // Angular 17+ puts the browser build in dist/browser, not dist.
        configuration.RootPath = "ClientApp/dist/browser";
    });

    services.AddSpaPrerenderingService<MySpaPrerenderingService>();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    if (!env.IsDevelopment())
    {
        app.UseSpaStaticFilesImproved();
    }

    app.UseRouting();
    app.UseEndpoints(endpoints => endpoints.MapControllers());

    app.UseSpaImproved(spa =>
    {
        spa.Options.SourcePath = "ClientApp";

        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
            options.BootModuleBuilder = env.IsDevelopment()
                ? new AngularPrerendererBuilder("build:ssr:development")
                : null;
            options.ExcludeUrls = ["/sockjs-node"];
        });

        if (env.IsDevelopment())
        {
            spa.UseAngularCliServer(npmScript: "start");
        }
    });
}
```

Your routes and per-page data live in one class:

```csharp
public class MySpaPrerenderingService : ISpaPrerenderingService
{
    private readonly ISpaRouteService spaRouteService;
    public MySpaPrerenderingService(ISpaRouteService spaRouteService)
        => this.spaRouteService = spaRouteService;

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("", "home")
            .Group("person", "person", person => person
                .Route("", "list")
                .Route("{id}", "show"));

        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
    {
        var route = await spaRouteService.GetCurrentRoute(httpContext);
        var people = httpContext.RequestServices.GetRequiredService<IPersonRepository>();

        switch (route?.Name)
        {
            case "person-list":
                data["people"] = await people.GetPeople();
                break;
            case "person-show":
                data["person"] = await people.GetPerson(Convert.ToInt32(route.Parameters["id"]));
                break;
        }
    }
}
```

`dotnet run` is all you need in development — the SPA middleware starts the Angular CLI dev server
itself. Do not run `ng serve` separately.

See the [Routing README](./MintPlayer.AspNetCore.SpaServices.Routing/README.md) for the Angular side
(how these `data` keys reach `main.server.ts`, and the matching client-side providers), and the
[Prerendering README](./MintPlayer.AspNetCore.SpaServices.Prerendering/README.md) for every option,
the logging categories, and troubleshooting.

## Running the demos

Two runnable samples live in [`Demo/`](./Demo):

| Demo | What it shows |
|---|---|
| [`Demo/Prerendering`](./Demo/Prerendering) | The full SSR setup — routes, per-route data, redirects, HTML minification. `dotnet run --project Demo/Prerendering/Demo.Web` |
| [`Demo/Xsrf`](./Demo/Xsrf) | CSRF token handling between ASP.NET Core and Angular. |

The first run builds the SPA and the SSR bundle, so give it a minute.


## MSBuild Integration

The packages automatically configure your project with MSBuild props and targets for SPA development. These are applied transitively to all projects that reference these packages.

### Properties

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

### Disabling SPA Builder

If your project references these packages but doesn't have a SPA, disable all build automation:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

### Custom SPA Root

If your SPA is in a different folder:

```xml
<PropertyGroup>
  <SpaRoot>src\frontend\</SpaRoot>
</PropertyGroup>
```

### Client-Only Builds (No SSR)

To skip SSR bundle during publish:

```xml
<PropertyGroup>
  <BuildServerSideRenderer>false</BuildServerSideRenderer>
</PropertyGroup>
```

## Server-side rendering background

If you have not set up SSR before, [this walkthrough](https://medium.com/@pieterjandeclippel/server-side-rendering-in-asp-net-core-angular-6df7adacbdaa)
covers the Angular side of the picture.

## Contributing

Build and test the whole solution with:

```
dotnet build MintPlayer.AspNetCore.SpaServices.sln
dotnet test MintPlayer.AspNetCore.SpaServices.Tests
```

Design notes and troubleshooting records for larger changes are kept in [`docs/`](./docs).

## License

Licensed under the [Apache License 2.0](https://opensource.org/licenses/Apache-2.0).
