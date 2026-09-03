# MintPlayer.AspNetCore.SpaServices.Xsrf

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Xsrf.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Xsrf.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

ASP.NET Core's antiforgery system expects a server-rendered form or view to emit the request token. A single-page application has neither, so the token never reaches the browser and every `[ValidateAntiForgeryToken]` endpoint rejects the SPA's requests. This package closes that gap with one middleware: on every response it obtains an antiforgery token pair and writes the request token to a JavaScript-readable `XSRF-TOKEN` cookie — exactly the cookie Angular's `HttpClient` looks for — so the SPA sends it back as a header and normal antiforgery validation just works.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Xsrf
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Xsrf
```

## How it works, end to end

1. **You register ASP.NET Core's antiforgery services** and tell them which request header carries the token: `services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN")`.
2. **You add this package's middleware** with `app.UseAntiforgeryGenerator()`.
3. On each request the middleware hooks `Response.OnStarting`. Just before response headers are sent it calls `IAntiforgery.GetAndStoreTokens(httpContext)`, which produces a token pair: a **cookie token** (written by the antiforgery system itself into its own `HttpOnly` cookie) and a **request token**.
4. The middleware appends the **request token** to a second cookie: name `XSRF-TOKEN`, `Path=/`, and — importantly — **`HttpOnly = false`**, so client-side JavaScript can read it. That is the whole point: an `HttpOnly` cookie would be invisible to the SPA.
5. **The SPA reads that cookie and echoes it in the configured header.** Angular's `HttpClient` does this automatically for same-origin mutating requests.
6. **Your endpoints validate as usual** with `[ValidateAntiForgeryToken]`, `[AutoValidateAntiforgeryToken]`, or a global filter. Validation compares the header's request token against the antiforgery cookie token; both must be present and must match.

The middleware only *issues* tokens. It never validates and never rejects a request — validation stays entirely with the framework's antiforgery attributes and filters.

## Public surface

The package exposes exactly one public API:

| Member | Description |
|--------|-------------|
| `AntiforgeryExtensions.UseAntiforgeryGenerator(this IApplicationBuilder builder)` | Adds the middleware that generates an XSRF token for the current user and stores it in a cookie named `XSRF-TOKEN`. |

There is no `AddXsrf()` or other service-registration helper — the services you need are the framework's own `AddAntiforgery()`. The middleware resolves `IAntiforgery` from DI, so **`AddAntiforgery()` (or something that includes it, such as `AddControllersWithViews()`/`AddMvc()`/`AddRazorPages()`) must be registered**, otherwise the pipeline throws on the first request.

## Cookie and header names

| Name | Value | Configurable? |
|------|-------|---------------|
| Request-token cookie written by this middleware | `XSRF-TOKEN` | **No.** The name, `Path=/` and `HttpOnly=false` are fixed in the middleware. |
| Request header read during validation | `X-XSRF-TOKEN` by convention | **Yes** — `AntiforgeryOptions.HeaderName`. It has no default; you must set it, or header-based validation won't be attempted at all. |
| Antiforgery's own cookie token cookie | `.AspNetCore.Antiforgery.<hash>` | Yes — `AntiforgeryOptions.Cookie`. This is *not* the cookie the SPA reads. |

Two consequences worth internalising:

- **`options.Cookie.Name` does not rename the SPA-visible cookie.** `AntiforgeryOptions.Cookie` configures the framework's internal cookie-token cookie. This middleware always writes `XSRF-TOKEN`. If you set `options.Cookie.Name = "CUSTOM-XSRF-TOKEN"` and then configure your SPA to read `CUSTOM-XSRF-TOKEN`, the SPA will read the *wrong* (and `HttpOnly`) cookie and validation will fail.
- So the only name you can change is the **header**. Change `AntiforgeryOptions.HeaderName` and the matching `headerName` in your SPA; leave the cookie name as `XSRF-TOKEN` on both sides.

Because the values happen to be Angular's defaults, an Angular app usually needs no client configuration at all.

## Complete example

### Program.cs (minimal hosting)

```csharp
using MintPlayer.AspNetCore.SpaServices.Xsrf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// The header the SPA will send the request token in. Required.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Issue the XSRF-TOKEN cookie on every response.
// Place it before the endpoints that will be validated.
app.UseAntiforgeryGenerator();

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

app.Run();
```

### Startup.cs (classic hosting)

```csharp
using MintPlayer.AspNetCore.SpaServices.Xsrf;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllersWithViews();
        services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseHttpsRedirection();
        app.UseAntiforgeryGenerator();

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllerRoute(
                name: "default",
                pattern: "{controller}/{action=Index}/{id?}");
        });
    }
}
```

### A validated endpoint

```csharp
[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    [HttpGet]
    public IEnumerable<WeatherForecast> Get() => WeatherForecast.Sample();

    [HttpPost]
    [ValidateAntiForgeryToken]   // requires a matching X-XSRF-TOKEN header
    public ActionResult CreateWeatherForecast() => Ok();
}
```

Validate only mutating verbs. `GET`/`HEAD`/`OPTIONS`/`TRACE` are not validated by `[AutoValidateAntiforgeryToken]`, and putting `[ValidateAntiForgeryToken]` on a `GET` action is a good way to break your own app.

### Angular: standalone bootstrap (`app.config.ts`)

XSRF protection is on by default in Angular's `HttpClient`; `withXsrfConfiguration` just makes the names explicit and self-documenting.

```typescript
import { ApplicationConfig } from '@angular/core';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(withXsrfConfiguration({
      cookieName: 'XSRF-TOKEN',
      headerName: 'X-XSRF-TOKEN'
    }))
  ]
};
```

### Angular: NgModule bootstrap

```typescript
import { NgModule } from '@angular/core';
import { HttpClientModule, HttpClientXsrfModule } from '@angular/common/http';

@NgModule({
  imports: [
    HttpClientModule,
    HttpClientXsrfModule.withOptions({
      cookieName: 'XSRF-TOKEN',
      headerName: 'X-XSRF-TOKEN'
    })
  ]
})
export class AppModule { }
```

### Angular: sending a request

Nothing special is needed in the component — the interceptor adds the header:

```typescript
import { Component, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

@Component({ selector: 'app-create-forecast', template: '<button (click)="create()">Create</button>' })
export class CreateForecastComponent {
  private readonly http = inject(HttpClient);

  create() {
    this.http.post('/WeatherForecast', {}).subscribe({
      error: (err) => console.error(err)
    });
  }
}
```

### Custom header name

If you change the header, change it in both places and leave the cookie name alone:

```csharp
services.AddAntiforgery(options => options.HeaderName = "X-CUSTOM-XSRF");
```

```typescript
provideHttpClient(withXsrfConfiguration({
  cookieName: 'XSRF-TOKEN',      // fixed by the middleware — do not change
  headerName: 'X-CUSTOM-XSRF'    // must match AntiforgeryOptions.HeaderName
}))
```

## Troubleshooting

### 400 Bad Request: "The required antiforgery header value ... is not present"

The request reached validation with no token header.

- Check that `AntiforgeryOptions.HeaderName` is set. It has **no default**; without it the framework doesn't look for a header at all.
- Check that the SPA's `headerName` matches it exactly (Angular's default is `X-XSRF-TOKEN`).
- Angular only attaches the header to **relative or same-origin** URLs. `this.http.post('/api/x', ...)` gets the header; `this.http.post('https://api.example.com/x', ...)` does not. If your SPA builds absolute URLs from a `<base href>`, keep them same-origin with the API.
- Angular also skips `GET` and `HEAD`. A validated `GET` will always fail.

### 400 Bad Request: token mismatch, or "The antiforgery cookie token and request token do not match"

Both tokens arrived but don't pair up.

- The most common cause is a renamed cookie: the SPA is reading `AntiforgeryOptions.Cookie.Name` instead of `XSRF-TOKEN`. Point it back at `XSRF-TOKEN`.
- Tokens are bound to the authenticated identity. If the user signs in or out between the response that issued the cookie and the request that uses it, the pair no longer matches. Because the middleware refreshes `XSRF-TOKEN` on every response, a plain reload or any subsequent request fixes this — but a SPA holding a stale in-memory copy of the token will not recover, so read it from the cookie at send time (as Angular's interceptor does).
- With multiple server instances, make sure the ASP.NET Core Data Protection keys are shared; otherwise instance B can't validate a token issued by instance A.

### The `XSRF-TOKEN` cookie is never set

- Is `app.UseAntiforgeryGenerator()` actually reached? Middleware placed after a terminal branch (`UseStaticFiles` short-circuiting for a static asset, a `Map`/`UseSpa` branch, or an endpoint that already wrote the response) never runs for that request. Register it early — right after `UseHttpsRedirection()` is a good spot — and before your SPA/static-file and endpoint middleware.
- The cookie is written from a `Response.OnStarting` callback, so it appears only on responses that actually start. Requests aborted before headers are flushed get nothing.
- Confirm `IAntiforgery` is registered (`AddAntiforgery()` directly, or via `AddControllersWithViews()`/`AddMvc()`/`AddRazorPages()`).

### The cookie is set but JavaScript can't read it

The middleware writes the cookie with `HttpOnly = false` precisely so the SPA can read it. If `document.cookie` doesn't show it, something else is intercepting: a reverse proxy or CDN rewriting `Set-Cookie`, a cookie-policy middleware, or `UseCookiePolicy` with a restrictive `MinimumSameSitePolicy`. Check the raw `Set-Cookie` response header in the browser's network tab.

### HTTPS and SameSite

The cookie is written with `Path=/` and without `Secure` or an explicit `SameSite` value, so the framework's cookie policy defaults apply.

- Keep the SPA and the API on the **same origin**. XSRF-cookie-to-header only works same-origin: with `SameSite=Lax` (the ASP.NET Core default) the cookie isn't sent on cross-site requests, and cross-origin JavaScript can't read it either. If you genuinely must split origins, you need a CORS design with credentials, `SameSite=None; Secure`, and `Access-Control-Allow-Headers` including your token header — at which point you should reconsider whether cookie-based antiforgery is the right tool.
- During development over `http://localhost`, `SameSite=None` cookies are rejected by browsers for lacking `Secure`. Prefer running the dev server over HTTPS (`app.UseHttpsRedirection()` plus the ASP.NET Core dev certificate) and stay on the same origin.
- Behind a TLS-terminating proxy, configure forwarded headers so the app knows the request was HTTPS; otherwise redirect and cookie behaviour will disagree with the browser's view of the connection.

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - Interfaces for integrating without the implementation
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration

## License

This project is licensed under the Apache 2.0 License.
