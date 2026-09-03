# MintPlayer.AspNetCore.SpaServices.Routing

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Routing.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

Your SPA knows its own routes; ASP.NET Core does not. During prerendering that is a problem: the server has to decide *which* data to hand to the SPA before the SPA has told it which page it is about to render. This package closes that gap. You describe your client-side route table once, in C#, and the server can then answer three questions about any incoming request: which client-side route does this URL activate, what are its parameters, and what URL would I build for a given route and set of parameters. That is enough to load exactly the data a page needs before rendering it, to redirect to a canonical URL without rendering the page you are about to throw away, and to generate SPA links from server code such as a sitemap generator. This is the package to install: it pulls in [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering), [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) and [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) with it.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Routing
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Routing
```

## Table of contents

- [How it fits together](#how-it-fits-together)
- [Registration](#registration)
- [Declaring your SPA routes](#declaring-your-spa-routes)
- [ISpaRouteService](#isparouteservice)
  - [GetCurrentRoute](#getcurrentroutehttpcontext)
  - [GenerateUrl](#generateurl)
  - [Redirect](#redirect)
- [End-to-end example](#end-to-end-example)
- [The Angular side: receiving the data](#the-angular-side-receiving-the-data)
- [Server-side URL generation](#server-side-url-generation)
- [MSBuild integration](#msbuild-integration)
- [Troubleshooting](#troubleshooting)
- [Related packages](#related-packages)
- [License](#license)

## How it fits together

There are exactly two things you write:

1. An implementation of `ISpaPrerenderingService`. It has two methods: `BuildRoutes`, where you
   declare your client-side route table, and `OnSupplyData`, which runs once per prerendered
   request and fills a dictionary that is handed to your SSR bundle.
2. The Angular side that reads that dictionary out of `params.data`.

`ISpaRouteService` is provided for you and is what you call from inside `OnSupplyData` (and from
anywhere else in your app, such as a controller) to match and generate URLs.

```
browser request  ->  UseSpaPrerendering  ->  your ISpaPrerenderingService.OnSupplyData
                                                    |
                                                    | ISpaRouteService.GetCurrentRoute(context)
                                                    | -> route name + parameters
                                                    | data["person"] = ...
                                                    v
                                             main.server.ts  (params.data)
                                                    |
                                                    v
                                             prerendered HTML
```

## Registration

Register your prerendering service with `AddSpaPrerenderingService<T>`. That single call also
registers `IHttpContextAccessor` and `ISpaRouteService`, so there is nothing else to add.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSpaPrerenderingService<Services.DemoSpaPrerenderingService>();
}
```

Your implementation is registered as a **scoped** service, so it can take scoped dependencies
(a DbContext, a repository, a per-request service) through the constructor.

`ISpaRouteService` itself is a **singleton**. It calls `BuildRoutes` lazily, once, on the first
request that needs a route table, and caches the result for the lifetime of the application. Two
consequences worth knowing:

- `BuildRoutes` must be pure route declaration. Do not try to vary the route table per request,
  per user, or per tenant - later requests will keep seeing the table built for the first one.
- `BuildRoutes` returns a `Task`, so you may `await` while building (loading route definitions from
  configuration, for example), but it happens only once.

## Declaring your SPA routes

`BuildRoutes` receives an `ISpaRouteBuilder` with two methods:

```csharp
ISpaRouteBuilder Route(string path, string name);
ISpaRouteBuilder Group(string path, string name, Action<ISpaRouteBuilder> builder);
```

Both return the builder, so calls chain. `Group` gives you a path prefix and a name prefix for the
routes declared inside its callback, and groups may be nested to any depth.

```csharp
public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
{
    routeBuilder
        .Route("", "home")
        .Group("person", "person", person_routes => person_routes
            .Route("", "list")
            .Route("create", "create")
            .Route("{personid}", "show")
            .Route("{personid}/edit", "edit")
            .Route("{personid}/{name}", "show-name")
            .Route("{personid}/{name}/edit", "edit-name")
        );

    return Task.CompletedTask;
}
```

### How names and paths compose

A nested route's **full name** is the parent's full name, a `-`, and the child's name. Its **full
path** is the parent's full path and the child's path joined with `/` - except that an empty child
path contributes nothing, so it resolves to the parent's path unchanged. The table above therefore
produces:

| Full name | Full path | Example URL |
|---|---|---|
| `home` | *(empty)* | `/` |
| `person` | `person` | `/person` |
| `person-list` | `person` | `/person` |
| `person-create` | `person/create` | `/person/create` |
| `person-show` | `person/{personid}` | `/person/42` |
| `person-edit` | `person/{personid}/edit` | `/person/42/edit` |
| `person-show-name` | `person/{personid}/{name}` | `/person/42/john-doe` |
| `person-edit-name` | `person/{personid}/{name}/edit` | `/person/42/john-doe/edit` |

The full name is the identifier you use everywhere else - the `route.Name` you switch on, and the
`routeName` you pass to `GenerateUrl` and `Redirect`. A group is itself a registered, matchable
route (`person` above), which is why an empty-path child inside a group is *also* registered at the
same path; the child (`person-list`) is matched first, so declaring a `Route("", ...)` inside a
group is the normal way to name a group's index page.

These paths mirror your Angular `Routes` array. Keeping the two in sync is your job - nothing
validates one against the other.

### Route parameters

- A `{placeholder}` matches one path segment: one or more characters that are not `/`. It cannot be
  empty and cannot span a `/`.
- Everything outside the placeholders is matched **literally**. A route path is not a regular
  expression, so `a.b` matches only `a.b`, and characters such as `(`, `+` or `?` in a path are
  plain characters.
- Matching is against the full path only, anchored at both ends. A trailing slash is not ignored.
- The query string is not part of the route path. Do not put `?foo=bar` in a route path; query
  parameters are surfaced separately (see `QueryParameters` below).

## ISpaRouteService

```csharp
public interface ISpaRouteService
{
    Task<SpaRoute> GetCurrentRoute(HttpContext httpContext);

    Task Redirect(HttpContext context, string routeName, Dictionary<string, object> parameters);
    Task Redirect<T>(HttpContext context, string routeName, T parameters);

    Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters);
    Task<string> GenerateUrl<T>(string routeName, T parameters);
    Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, HttpContext httpContext);
    Task<string> GenerateUrl<T>(string routeName, T parameters, HttpContext httpContext);
    Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host);
    Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host);
    Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host, string fragment);
    Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment);
}
```

Inject it like any other service:

```csharp
public partial class DemoSpaPrerenderingService : ISpaPrerenderingService
{
    private readonly ISpaRouteService spaRouteService;

    public DemoSpaPrerenderingService(ISpaRouteService spaRouteService)
        => this.spaRouteService = spaRouteService;
}
```

### GetCurrentRoute(HttpContext)

Matches the requested URL against the registered route table and returns the first route that
matches, or `null` when nothing matches.

```csharp
public class SpaRoute
{
    public string Name { get; set; }                            // full name, e.g. "person-show-name"
    public string Path { get; set; }                            // full path, e.g. "person/{personid}/{name}"
    public Dictionary<string, string> Parameters { get; set; }
    public Dictionary<string, string> QueryParameters { get; set; }
}
```

Note that it reads the **raw request target** rather than `HttpContext.Request.Path`. That matters:
in Production the SPA fallback has already rewritten the path to `index.html`, so
`Request.Path` would tell you nothing, while `GetCurrentRoute` still sees the URL the user visited.

Worked example - reading both dictionaries:

```csharp
// GET /person/42/john%20doe?tab=history&debug
var route = await spaRouteService.GetCurrentRoute(context);

route.Name;                         // "person-show-name"
route.Path;                         // "person/{personid}/{name}"
route.Parameters["personid"];       // "42"
route.Parameters["name"];           // "john doe"   (percent-decoded for you)
route.QueryParameters["tab"];       // "history"
route.QueryParameters["debug"];     // null         (key present, no '=')
```

Details that will save you a debugging session:

- **Values are strings.** Convert them yourself: `Convert.ToInt32(route.Parameters["personid"])`.
- **Values are already percent-decoded.** Do not decode them again. A `+` is left as a literal `+`,
  not read as a space.
- `Parameters` is empty (never `null`) for a route with no placeholders.
- `QueryParameters` is always populated, including for the root route, and is empty when there is no
  query string. A key repeated in the query string is last-one-wins. A key with no `=` maps to
  `null`, so use `TryGetValue` or a null check rather than assuming a value.
- The declaration order of your routes decides which one wins when two could match, with nested
  routes tried before their parent group. Declare the specific route before the greedy one.

### GenerateUrl

Builds a URL for a route name and a set of parameter values. Placeholders in the route path are
replaced with the matching values; **any parameter the route path does not declare is appended as a
query-string entry**. Values are percent-encoded on the way out, which makes generate/parse a
faithful round trip - a value containing a space, `/`, `&`, `?` or `%` comes back out of
`GetCurrentRoute` unchanged.

The dictionary form and the anonymous-type form are equivalent:

```csharp
// Dictionary
var parms = new Dictionary<string, object> { ["personid"] = 42, ["name"] = "john-doe" };
var url = await spaRouteService.GenerateUrl("person-edit-name", parms);
// "/person/42/john-doe/edit"

// Anonymous type - the property names are the parameter names
var url = await spaRouteService.GenerateUrl("person-edit-name", new { personid = 42, name = "john-doe" });
// "/person/42/john-doe/edit"
```

Extra parameters become a query string:

```csharp
var parms = new Dictionary<string, object> { ["personid"] = 42, ["tab"] = "a b" };
var url = await spaRouteService.GenerateUrl("person-edit", parms);
// "/person/42/edit?tab=a%20b"
```

A route with no parameters still needs an (empty) argument, and the dictionary overload is the
clearest way to say so:

```csharp
var url = await spaRouteService.GenerateUrl("person-list", new Dictionary<string, object>());
// "/person"
```

#### Absolute URLs

The two-argument overloads return a site-relative path (`/person/42/edit`). For an absolute URL,
use one of the longer overloads:

```csharp
// From the current request: scheme, host and PathBase are taken from it
var abs = await spaRouteService.GenerateUrl("person-edit", new { personid = 42 }, context);
// "https://localhost:5001/person/42/edit"

// Explicit protocol and host (no PathBase is applied)
var abs = await spaRouteService.GenerateUrl("person-edit", new { personid = 42 }, "https", "example.com");
// "https://example.com/person/42/edit"

// ... plus a hash fragment
var abs = await spaRouteService.GenerateUrl("person-show", new { personid = 42 }, "https", "example.com", "contact");
// "https://example.com/person/42#contact"
```

Only the `HttpContext` overloads apply `Request.PathBase`. If your app is hosted under a virtual
path, prefer them, or prepend the base yourself.

#### Watch the overload you actually bind to

`GenerateUrl<T>` reads the **public properties** of whatever you pass it. If you hand it something
typed as `IDictionary<string, object>` (rather than `Dictionary<string, object>`), the generic
overload is selected and you get the dictionary's own properties - `Count`, `Keys`, `Values` -
treated as route parameters, which fails in a confusing way. Keep your variable typed as
`Dictionary<string, object>`, or cast at the call site:

```csharp
IDictionary<string, object> loose = new Dictionary<string, object> { ["personid"] = 42 };

var wrong = await spaRouteService.GenerateUrl("person-edit", loose);                              // generic overload
var right = await spaRouteService.GenerateUrl("person-edit", (Dictionary<string, object>)loose);  // dictionary overload
```

#### Errors

- An unknown `routeName` throws `SpaRouteNotFoundException` (in
  `MintPlayer.AspNetCore.SpaServices.Routing.Exceptions`). The route name is the **full** name, so
  `person-edit`, not `edit`.
- A placeholder with no matching key throws `KeyNotFoundException`. Every `{placeholder}` in the
  route path must have a key, and keys are matched case-sensitively.
- A `null` value encodes to an empty string, producing a URL such as `/person//edit`, which will
  not match the route again. Guard against nulls before generating.

### Redirect

```csharp
Task Redirect(HttpContext context, string routeName, Dictionary<string, object> parameters);
Task Redirect<T>(HttpContext context, string routeName, T parameters);
```

Both overloads generate the URL exactly as `GenerateUrl` does, and then set up the redirect. Use
these from inside `OnSupplyData` rather than calling `Response.Redirect` yourself - what they do is
subtler than it looks, and all three parts matter:

1. **The redirect is assigned inside a `Response.OnStarting` callback**, not immediately. Other
   middleware (and your own later code) can still assign a status code after `OnSupplyData` has
   run; by deferring to the moment the response actually starts, this redirect is not overwritten
   by code that runs in between.
2. **It redirects permanently (301)**, because `Response.Redirect` defaults to 302 and, in a
   deferred callback, a 302 would clobber a status code that had been set in the meantime. Choose
   these methods only where a permanent redirect is what you mean - canonicalising a URL, for
   instance. For a temporary redirect, set the status and `Location` header yourself (and read
   point 3 first).
3. **It calls `HttpContext.SkipPrerendering()`.** The prerendering middleware decides whether to
   prerender by looking at the response status code, and it makes that decision *before* an
   `OnStarting` callback has run. Without this call, the middleware sees a 200, prerenders the
   page - a full SSR render, with all the data loading behind it - and then throws the result away
   because the response turns out to be a redirect.

```csharp
public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
{
    var route = await spaRouteService.GetCurrentRoute(context);

    if (route?.Name == "home")
    {
        // 301 to /person, no prerender wasted on the page nobody will see
        await spaRouteService.Redirect(context, "person-list", new Dictionary<string, object>());
        return;
    }

    // canonicalise /person/42 -> /person/42/john-doe
    if (route?.Name == "person-show")
    {
        var person = await personService.GetPerson(Convert.ToInt32(route.Parameters["personid"]));
        await spaRouteService.Redirect(context, "person-show-name", new { personid = person.Id, name = Slugify(person) });
        return;
    }
}
```

#### If you write your own deferred status change, call SkipPrerendering() yourself

This is the part people get wrong. Any code that assigns its status code from inside a
`Response.OnStarting` callback is invisible to the prerendering middleware, and there is no general
way for the middleware to detect it. **You must say so explicitly**, or the request is prerendered
and the rendered body is discarded (or, worse, sent along with your status code):

```csharp
using MintPlayer.AspNetCore.SpaServices.Prerendering;   // for SkipPrerendering()

// A 302 instead of the built-in 301, or a 404 - anything deferred to OnStarting
var url = await spaRouteService.GenerateUrl("person-list", new Dictionary<string, object>());

context.SkipPrerendering();
context.Response.OnStarting(() =>
{
    context.Response.StatusCode = StatusCodes.Status302Found;
    context.Response.Headers.Location = url;
    return Task.CompletedTask;
});
```

A status code you assign **directly** (not in a callback) needs no such call - the middleware sees
it. `HttpContext.IsPrerenderingSkipped()` is available if you need to check whether the flag has
already been set.

## End-to-end example

Registration:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddControllersWithViews();

    services.AddSpaStaticFilesImproved(configuration =>
    {
        // Angular 17+ splits its output into dist/browser and dist/server
        configuration.RootPath = "ClientApp/dist/browser";
    });

    services.AddSpaPrerenderingService<Services.DemoSpaPrerenderingService>();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    if (!env.IsDevelopment())
    {
        app.UseSpaStaticFilesImproved();
    }

    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllerRoute(name: "default", pattern: "{controller}/{action=Index}/{id?}");
    });

    app.UseSpaImproved(spa =>
    {
        spa.Options.SourcePath = "ClientApp";

        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
            options.BootModuleBuilder = env.IsDevelopment()
                ? new AngularPrerendererBuilder("build:ssr:development", @"Build at\:", 1)
                : null;
            options.ExcludeUrls = ["/sockjs-node"];
        });

        if (env.IsDevelopment())
        {
            spa.UseAngularCliServer(npmScript: "start", cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
        }
    });
}
```

The prerendering service - route table and per-route data loading in one class:

```csharp
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

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("", "home")
            .Group("person", "person", person_routes => person_routes
                .Route("", "list")
                .Route("create", "create")
                .Route("{personid}", "show")
                .Route("{personid}/edit", "edit")
                .Route("{personid}/{name}", "show-name")
                .Route("{personid}/{name}/edit", "edit-name")
            );

        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
    {
        var route = await spaRouteService.GetCurrentRoute(context);

        switch (route?.Name)
        {
            case "home":
                await spaRouteService.Redirect(context, "person-list", new Dictionary<string, object>());
                break;

            case "person-list":
                data["people"] = await personService.GetPeople();
                break;

            case "person-show":
            case "person-edit":
                {
                    // No slug in the URL yet - load the person and redirect to the canonical URL.
                    var personid = Convert.ToInt32(route.Parameters["personid"]);
                    var person = await personService.GetPerson(personid, false);
                    if (person == null)
                    {
                        context.SkipPrerendering();
                        context.Response.OnStarting(() =>
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return Task.CompletedTask;
                        });
                    }
                    else
                    {
                        await spaRouteService.Redirect(context, $"{route.Name}-name",
                            new { personid, name = Slugify(person) });
                    }
                }
                break;

            case "person-show-name":
            case "person-edit-name":
                {
                    var personid = Convert.ToInt32(route.Parameters["personid"]);
                    var person = await personService.GetPerson(personid);
                    if (person == null)
                    {
                        context.SkipPrerendering();
                        context.Response.OnStarting(() =>
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return Task.CompletedTask;
                        });
                    }
                    else if (route.Parameters["name"] == Slugify(person))
                    {
                        // The slug is correct - this is the canonical URL, so supply the data.
                        data["person"] = person;
                    }
                    else
                    {
                        // Someone changed the slug - redirect to the correct one.
                        await spaRouteService.Redirect(context, route.Name,
                            new { personid, name = Slugify(person) });
                    }
                }
                break;
        }

        data["message"] = "Message from server";
    }

    // Your own slug helper, e.g. "John Doe" -> "john-doe"
    private static string Slugify(Person person)
        => $"{person.FirstName} {person.LastName}".ToLowerInvariant().Replace(' ', '-');
}
```

Two things worth copying from that shape:

- Switch on `route?.Name` with the `?`. `GetCurrentRoute` can return `null`, and a URL that is not
  a SPA route at all (an API path, a static file) should simply supply no data.
- Load only what the matched route needs. `OnSupplyData` runs on every prerendered request, so an
  unconditional "load everything" here is a per-request cost on every page.

## The Angular side: receiving the data

Everything you put in the `data` dictionary arrives in your SSR entry point as `params.data`,
serialized to JSON. `main.server.ts` turns those keys into Angular providers:

```typescript
import 'reflect-metadata';
import { renderApplication } from '@angular/platform-server';
import { enableProdMode, StaticProvider } from '@angular/core';
import { createServerRenderer } from 'aspnet-prerendering';
import { APP_BASE_HREF } from '@angular/common';
import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { config as serverConfig } from './app/app.config.server';
import { MESSAGE_TOKEN, PERSON_TOKEN, PEOPLE_TOKEN } from './app/tokens';

enableProdMode();

export default createServerRenderer(params => {
  const providers: StaticProvider[] = [
    { provide: APP_BASE_HREF, useValue: params.origin + params.baseUrl.slice(0, -1) },
    { provide: MESSAGE_TOKEN, useValue: params.data.message },
  ];

  // Only the matched route supplied these, so provide a null fallback for the others.
  if ('people' in params.data) {
    providers.push({ provide: PEOPLE_TOKEN, useValue: params.data.people });
  } else {
    providers.push({ provide: PEOPLE_TOKEN, useValue: null });
  }
  if ('person' in params.data) {
    providers.push({ provide: PERSON_TOKEN, useValue: params.data.person });
  } else {
    providers.push({ provide: PERSON_TOKEN, useValue: null });
  }

  const options = {
    document: params.data.originalHtml,
    url: params.url,
    platformProviders: providers
  };

  return renderApplication(
    (context) => bootstrapApplication(App, serverConfig, context),
    options
  ).then(html => ({ html }));
});
```

Notes on that file:

- `params.data.originalHtml` is supplied by the prerendering middleware, not by you - it is the
  `index.html` shell to render into. Do not overwrite that key from `OnSupplyData`.
- `params.url`, `params.origin` and `params.baseUrl` likewise come from the middleware.
- Declare the tokens in one shared file so the server and the browser agree on them:

```typescript
// app/tokens.ts
import { InjectionToken } from '@angular/core';
import { Person } from './entities/person';

export const MESSAGE_TOKEN = new InjectionToken<string>('MESSAGE');
export const PERSON_TOKEN = new InjectionToken<Person>('PERSON');
export const PEOPLE_TOKEN = new InjectionToken<Person[]>('PEOPLE');
```

### Every key needs a browser-side answer too

This is the other half of the contract, and it is easy to forget. `main.server.ts` runs only during
prerendering. In the browser those providers do not exist, so a component that injects one of these
tokens will fail on the client unless the browser build answers for it as well. There are two ways
to do that, and the demo uses both.

**Either provide a browser-side value.** In a modern standalone Angular app, `main.ts` just
bootstraps, and the browser providers live in your browser config:

```typescript
// main.ts
import { bootstrapApplication } from '@angular/platform-browser';
import { config } from './app/app.config.browser';
import { App } from './app/app';

bootstrapApplication(App, config)
  .catch((err) => console.error(err));
```

```typescript
// app/app.config.browser.ts
import { mergeApplicationConfig, ApplicationConfig } from '@angular/core';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';
import { appConfig } from './app.config';
import { MESSAGE_TOKEN } from './tokens';

const browserConfig: ApplicationConfig = {
  providers: [
    provideClientHydration(withEventReplay()),
    { provide: MESSAGE_TOKEN, useValue: 'Message from browser' }
  ]
};

export const config = mergeApplicationConfig(appConfig, browserConfig);
```

**Or inject optionally and fall back to an HTTP call.** This is the right choice for page data,
because the value genuinely does not exist on a client-side navigation - the user arrived at the
page through the router, not through a server render:

```typescript
export class PersonListComponent {
  private readonly personService = inject(PersonService);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly peopleInj = inject(PEOPLE_TOKEN, { optional: true });

  people = signal<Person[]>([]);

  constructor() {
    if (isPlatformServer(this.platformId)) {
      this.people.set(this.peopleInj ?? []);
    } else {
      this.personService.getPeople().subscribe(people => this.people.set(people));
    }
  }
}
```

What you must not do is inject a token non-optionally that only `main.server.ts` provides. That
works on the first (prerendered) load and breaks the moment the user navigates client-side.

Whatever goes into `data` must also be JSON-serializable: DTOs and plain values, not entities with
lazy navigation properties or circular references.

## Server-side URL generation

`GenerateUrl` is useful well outside prerendering - anywhere server code needs a link into the SPA.
Inject `ISpaRouteService` into a controller, a background job or a Razor page as usual.

### Sitemap

```csharp
[Route("sitemap.xml")]
public async Task<IActionResult> Sitemap()
{
    var people = await personService.GetPeople();

    var urls = new List<string>
    {
        await spaRouteService.GenerateUrl("person-list", new Dictionary<string, object>(), Request.Scheme, Request.Host.Value)
    };

    foreach (var person in people)
    {
        urls.Add(await spaRouteService.GenerateUrl(
            "person-show-name",
            new { personid = person.Id, name = Slugify(person) },
            Request.Scheme,
            Request.Host.Value));
    }

    var sitemap = new XDocument(
        new XElement(XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9") + "urlset",
            urls.Select(u => new XElement(XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9") + "url",
                new XElement(XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9") + "loc", u)))));

    return Content(sitemap.ToString(), "application/xml");
}
```

### OpenSearch-style redirect

A search endpoint that hands the browser off to the SPA. The dictionary form is convenient here,
because a key the route does not declare becomes a query-string parameter automatically:

```csharp
[HttpGet("search")]
public async Task<IActionResult> Search([FromQuery] string q)
{
    var parms = new Dictionary<string, object> { ["term"] = q };
    var url = await spaRouteService.GenerateUrl("person-list", parms);
    // "/person?term=john%20doe"

    return Redirect(url);
}
```

Outside the prerendering pipeline a plain `Redirect(...)` is fine - there is no prerendering
decision to defer to, so none of the `SkipPrerendering` machinery applies.

### Which form to use

- **Anonymous type** (`new { personid = 42 }`) - reads best when you know the parameters at compile
  time. Property names are the parameter names.
- **Dictionary** - use it when the keys are dynamic, when you are adding optional query parameters
  conditionally, or when there are no parameters at all.

## MSBuild integration

Because this package brings in
[MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices),
your project also picks up its build targets, which run `npm install` and build the SPA (including
the SSR bundle) as part of your build and publish. The most commonly overridden properties:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSpaBuilder` | `true` | Master switch for all SPA build automation |
| `SpaRoot` | `ClientApp\` | Path to your SPA source folder |
| `BuildServerSideRenderer` | `true` | Whether to build the SSR bundle during publish |

If a project references this package but has no SPA of its own (a class library that only needs
`ISpaRouteService`, say), turn the automation off:

```xml
<PropertyGroup>
  <EnableSpaBuilder>false</EnableSpaBuilder>
</PropertyGroup>
```

The full property list is documented with the
[NodeServices package](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices).

## Troubleshooting

### `GetCurrentRoute` returns null

Work down this list:

1. **The route is not registered.** Only what you declared in `BuildRoutes` can match. A route that
   exists in your Angular `Routes` array but not in `BuildRoutes` returns `null` here.
2. **Trailing slash.** Matching is anchored and literal, so `/person/` does not match the route
   whose path is `person`. Either normalise the URL with a redirect earlier in the pipeline or
   declare both.
3. **A parameter does not match.** A `{placeholder}` requires one or more non-`/` characters. So
   `/person//edit` does not match `person/{personid}/edit` (the segment is empty), and
   `/person/42/extra/edit` does not either (the value would have to span a `/`). For a value that
   legitimately contains a slash, split it into two placeholders or percent-encode it - `GenerateUrl`
   already does the latter for you.
4. **Segment count.** `person/{personid}` matches exactly two segments. `/person/42/john-doe` needs
   its own route (`person/{personid}/{name}`).
5. **Literal text really is literal.** A `.`, `(` or `+` in a route path matches only itself, so
   check for a typo rather than assuming a pattern.
6. **Route order.** If you get a *different* route than you expected rather than `null`, an earlier,
   greedier declaration matched first. Nested routes are matched before their parent group, and
   otherwise declaration order wins - so declare `person/create` before `person/{personid}`.

A quick way to see the table your app actually built is to log it from a scratch endpoint by
generating a URL for each name you expect and observing which ones throw.

### `GenerateUrl` throws

- `SpaRouteNotFoundException: Route with name X not found.` - the name is wrong. Remember that it is
  the composed **full** name (`person-edit`, not `edit`), that `Group` contributes its name as a
  prefix, and that names are case-sensitive.
- `KeyNotFoundException` - the route path has a `{placeholder}` with no corresponding key in your
  parameters. Check spelling and casing: the dictionary lookup is case-sensitive, so `personId`
  will not satisfy `{personid}`. With the anonymous-type overload, the *property name* must match
  the placeholder exactly.
- A URL with an empty segment (`/person//edit`) - one of your values was `null`. A `null` encodes to
  an empty string rather than throwing, so validate before generating.
- Unexpected query-string entries - any key not present in the route path is appended to the query
  string by design. If you see `?Count=0&Keys=...`, you passed a dictionary through the generic
  overload; see [Watch the overload you actually bind to](#watch-the-overload-you-actually-bind-to).

### A redirect returns a prerendered body

The response has a 3xx status but also a full HTML page, and the server spent time rendering it.
That is the signature of a deferred status change the prerendering middleware could not see. Use
`ISpaRouteService.Redirect`, which handles it, or - if you set the status yourself inside a
`Response.OnStarting` callback - call `context.SkipPrerendering()` alongside it. The same applies to
a deferred 404 or 410. See [Redirect](#redirect).

### The data never reaches the SPA

- Check that `OnSupplyData` really ran and really matched: log `route?.Name` at the top.
- Check the key names. `data["people"]` in C# is `params.data.people` in `main.server.ts` - the
  dictionary keys are used verbatim.
- Check that the value serializes to JSON. An entity with circular references or lazy-loaded
  navigation properties will fail; project to a DTO first.
- Check that your SSR bundle is the one being loaded (`options.BootModulePath`) and has been
  rebuilt after your changes.

### Everything works in Development and breaks in Production

Usually the static-files root: Angular 17+ emits `dist/browser` and `dist/server`, so
`RootPath` must point at `ClientApp/dist/browser` and `BootModulePath` at
`ClientApp/dist/server/main.js`. Route matching itself is not affected by the environment -
`GetCurrentRoute` reads the raw request target, so it keeps working after the SPA fallback has
rewritten the path to `index.html`.

## Related Packages

- [MintPlayer.AspNetCore.NodeServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.NodeServices) - Node.js integration and the SPA build targets
- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services, static files and the Angular CLI dev server
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - The prerendering middleware, `ISpaPrerenderingService` and `SkipPrerendering()`
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - Shared abstractions
- [MintPlayer.AspNetCore.SpaServices.Xsrf](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf) - XSRF token support for SPAs

Source, issues and the runnable demo: [github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices)

## License

This project is licensed under the Apache 2.0 License.
