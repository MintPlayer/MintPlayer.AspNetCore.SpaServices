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
4. The middleware appends the **request token** to a second cookie: name `XSRF-TOKEN`, `Path=/`, `SameSite=Strict`, `Secure` on HTTPS requests, and — importantly — **`HttpOnly = false`**, so client-side JavaScript can read it. That is the whole point: an `HttpOnly` cookie would be invisible to the SPA.
5. **The SPA reads that cookie and echoes it in the configured header.** Angular's `HttpClient` does this automatically for same-origin mutating requests.
6. **Your endpoints validate as usual** with `[ValidateAntiForgeryToken]`, `[AutoValidateAntiforgeryToken]`, or a global filter. Validation compares the header's request token against the antiforgery cookie token; both must be present and must match.

The middleware only *issues* tokens. It never validates and never rejects a request — validation stays entirely with the framework's antiforgery attributes and filters.

## Public surface

| Member | Description |
|--------|-------------|
| `AntiforgeryExtensions.UseAntiforgeryGenerator(this IApplicationBuilder builder)` | Adds the middleware that generates an XSRF token for the current user and stores it in a cookie named `XSRF-TOKEN`. |
| `AntiforgeryExtensions.UseAntiforgeryGenerator(this IApplicationBuilder builder, Action<XsrfOptions> configure)` | The same, with the cookie and the cache-header policy configured. |
| `XsrfOptions` | `Cookie` (a read-only `CookieBuilder`: name, path, `SameSite`, `SecurePolicy`, domain) and `CacheHeaders`. |
| `XsrfCacheHeaderPolicy` | `PreservePrivate` (default) or `NoStore`. See [Caching](#caching). |

```csharp
app.UseAntiforgeryGenerator(options =>
{
    options.Cookie.Name = "CUSTOM-XSRF";                     // configure the properties
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // ...not the builder
});
```

`Cookie` is deliberately **get-only**. The hardened defaults live in its initialiser, so assigning a
fresh `CookieBuilder` — the obvious-looking way to rename the cookie — would silently reset
`SameSite` to `Unspecified` and hand the choice back to the browser, which is the very defect this
release fixes. Setting the properties keeps everything you did not mention.

Configurations that cannot work are rejected at startup rather than left to be diagnosed from a 400
much later: an `HttpOnly` cookie the SPA could not read, or `SameSite=None` without `Secure`, each
throw an `ArgumentException` from `UseAntiforgeryGenerator`.

There is no `AddXsrf()` or other service-registration helper — the services you need are the framework's own `AddAntiforgery()`. The middleware resolves `IAntiforgery` from DI, so **`AddAntiforgery()` (or something that includes it, such as `AddControllersWithViews()`/`AddMvc()`/`AddRazorPages()`) must be registered**, otherwise the pipeline throws on the first request.

## Cookie and header names

| Name | Value | Configurable? |
|------|-------|---------------|
| Request-token cookie written by this middleware | `XSRF-TOKEN` | **Yes** — `XsrfOptions.Cookie.Name`, via the configuring overload. `HttpOnly=false` is fixed: an `HttpOnly` cookie would be invisible to the SPA, so setting it is rejected at startup. |
| Request header read during validation | `X-XSRF-TOKEN` by convention | **Yes** — `AntiforgeryOptions.HeaderName`. It has no default; you must set it, or header-based validation won't be attempted at all. |
| Antiforgery's own cookie token cookie | `.AspNetCore.Antiforgery.<hash>` | Yes — `AntiforgeryOptions.Cookie`. This is *not* the cookie the SPA reads. |

Two consequences worth internalising:

- **`AntiforgeryOptions.Cookie.Name` does not rename the SPA-visible cookie.** It configures the framework's internal cookie-token cookie. If you set `AntiforgeryOptions.Cookie.Name = "CUSTOM-XSRF-TOKEN"` and then configure your SPA to read `CUSTOM-XSRF-TOKEN`, the SPA will read the *wrong* (and `HttpOnly`) cookie and validation will fail. To rename the cookie the SPA reads, use **this package's** `XsrfOptions.Cookie.Name` and the matching `cookieName` in your SPA.
- The two are separate cookies with separate options. `AntiforgeryOptions.Cookie` is the `HttpOnly` half the server validates against; `XsrfOptions.Cookie` is the script-readable half the SPA echoes back.

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

- Is `app.UseAntiforgeryGenerator()` actually reached? Middleware placed after a terminal branch (`UseStaticFiles` short-circuiting for a static asset, a `Map`/`UseSpa` branch, or an endpoint that already wrote the response) never runs for that request. For static assets that is deliberate — see [Where to register it](#where-to-register-it) — but it must still sit **before whatever serves your SPA's HTML entry point**, or the app loads with no cookie and the first mutating request is rejected.
- The cookie is written from a `Response.OnStarting` callback, so it appears only on responses that actually start. Requests aborted before headers are flushed get nothing.
- Confirm `IAntiforgery` is registered (`AddAntiforgery()` directly, or via `AddControllersWithViews()`/`AddMvc()`/`AddRazorPages()`).

### The cookie is set but JavaScript can't read it

The middleware writes the cookie with `HttpOnly = false` precisely so the SPA can read it. If `document.cookie` doesn't show it, something else is intercepting: a reverse proxy or CDN rewriting `Set-Cookie`, a cookie-policy middleware, or `UseCookiePolicy` with a restrictive `MinimumSameSitePolicy`. Check the raw `Set-Cookie` response header in the browser's network tab.

### HTTPS and SameSite

The cookie is written with `Path=/`, `SameSite=Strict`, and `Secure` whenever the request arrived over HTTPS (`CookieSecurePolicy.SameAsRequest`). Change either through the configuring overload.

> Before 11.0.0-rc.2 the cookie carried neither attribute, and the README claimed "the framework's cookie policy defaults apply". That was wrong: no cookie policy applies unless the application calls `UseCookiePolicy()`, so the attributes were simply absent and the browser's own `SameSite` default took over. See [issue #85](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/85).

- **Behind a TLS-terminating reverse proxy, `Secure` is silently absent** unless you enable forwarded headers. `SameAsRequest` is evaluated against `HttpContext.Request.IsHttps`, which is `false` when nginx, Traefik, or a container ingress terminated the TLS — even though the browser connected over HTTPS. Add `UseForwardedHeaders` with `ForwardedHeaders.XForwardedProto` (and set `KnownProxies`/`KnownNetworks` for your topology; the defaults only trust loopback), or set `SecurePolicy = CookieSecurePolicy.Always`. The middleware logs a warning once per process when it detects this.
- `CookieSecurePolicy.Always` is not the default because a browser refuses a `Secure` cookie from a plain-HTTP origin other than `localhost`, which would break HTTP-only intranet and LAN-address development outright.
- Keep the SPA and the API on the **same origin**. XSRF-cookie-to-header only works same-origin: with `SameSite=Lax` (the ASP.NET Core default) the cookie isn't sent on cross-site requests, and cross-origin JavaScript can't read it either. If you genuinely must split origins, you need a CORS design with credentials, `SameSite=None; Secure`, and `Access-Control-Allow-Headers` including your token header — at which point you should reconsider whether cookie-based antiforgery is the right tool.
- During development over `http://localhost`, `SameSite=None` cookies are rejected by browsers for lacking `Secure`. Prefer running the dev server over HTTPS (`app.UseHttpsRedirection()` plus the ASP.NET Core dev certificate) and stay on the same origin.
- Behind a TLS-terminating proxy, configure forwarded headers so the app knows the request was HTTPS; otherwise redirect and cookie behaviour will disagree with the browser's view of the connection.

## Where to register it

One rule: **before whatever serves the SPA's HTML entry point.** Angular attaches no antiforgery header until the cookie exists, so if the navigation that loads the app does not mint one, the first mutating request is rejected.

It does **not** have to come before `UseStaticFiles()`, and usually should not. Static-file middleware short-circuits for a file it can serve, so anything registered after it never runs for those responses — which is what you want:

```csharp
app.UseHttpsRedirection();
app.UseStaticFiles();            // fingerprinted assets short-circuit here...
app.UseAntiforgeryGenerator();   // ...so they keep public, max-age=31536000
app.UseRouting();
app.UseEndpoints(/* ... */);
app.UseSpaImproved(/* serves index.html, and does pass through the mint */);
```

Registered *before* `UseStaticFiles()`, every asset response also carries a per-user `Set-Cookie: XSRF-TOKEN` and has its `Cache-Control` forced to `private` (see [Caching](#caching)), so `public, max-age=31536000` on a hashed bundle becomes `max-age=31536000, private` and drops out of shared caches. Correct, but wasteful — the CDN-cacheability of your static assets is decided by where you put this line.

### Endpoint-routed static assets

`MapStaticAssets()` serves from `UseEndpoints`, so those responses are reached *after* this middleware wherever you put it — `UseStaticFiles` short-circuits, an endpoint does not. To exempt them, [short-circuit the endpoint](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files) **and** register the generator below `UseRouting()`:

```csharp
app.UseRouting();
app.UseAntiforgeryGenerator();                            // must be BELOW UseRouting
app.UseEndpoints(e => e.MapStaticAssets().ShortCircuit());
```

The ordering condition is not cosmetic. Short-circuiting takes effect once routing has *matched*, so it can only skip middleware registered after `UseRouting()`. Put the generator above it and the middleware has already run and already registered its `Response.OnStarting` callback — and that callback fires when the response starts regardless of what short-circuited afterwards, so the cookie still goes out and the cache headers are still touched.

Measured on a real server, `GET` of a short-circuited endpoint:

| Generator | `Set-Cookie` | `Cache-Control` |
|---|---|---|
| above `UseRouting()` | both cookies written | overwritten by the mint |
| below `UseRouting()` | none | untouched |

The SPA's HTML entry point is an endpoint too, so it still mints in the second arrangement — the two requirements compose. Which of these you want is your application's call; the library has no opinion and does nothing about it on your behalf.

## Caching

`IAntiforgery.GetAndStoreTokens` stamps `Cache-Control: no-cache, no-store` and `Pragma: no-cache` whenever the response has not started — and inside a `Response.OnStarting` callback it never has, because the server commits headers only after the callbacks have run. Kestrel also fires those callbacks last-in-first-out, so this middleware, registered early as recommended, gets the **final** word over everything else in the pipeline.

Before 11.0.0-rc.2 that made every response passing through `UseAntiforgeryGenerator()` uncacheable, silently, including responses whose caching policy the application had deliberately set — and including prerendered HTML from `MintPlayer.AspNetCore.SpaServices.Prerendering`, which goes to some trouble to preserve it.

The default is now `XsrfCacheHeaderPolicy.PreservePrivate`:

| The response had | Result |
|---|---|
| No `Cache-Control` of its own | `no-cache, no-store` — unchanged. The SPA's HTML navigation carries a fresh token and is still never cached. |
| `Cache-Control: public, max-age=300` | `max-age=300, private` — preserved, but forced `private`. |
| `Cache-Control: no-store` | preserved. |

`private` is forced and `public` dropped because the response carries a `Set-Cookie` holding *this* user's token; a shared cache storing it could hand that token to the next visitor. Set `options.CacheHeaders = XsrfCacheHeaderPolicy.NoStore` to restore the pre-11.0.0-rc.2 behaviour.

## What this package does not protect

- **The framework's own antiforgery cookie ships without `Secure`, even over HTTPS.** `AntiforgeryOptions.Cookie.SecurePolicy` defaults to `CookieSecurePolicy.None`, and that cookie belongs to `AddAntiforgery`, not to this package. If you serve over HTTPS everywhere, set it yourself:

  ```csharp
  if (!builder.Environment.IsDevelopment())
  {
      builder.Services.AddAntiforgery(options =>
      {
          options.HeaderName = "X-XSRF-TOKEN";
          options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
      });
  }
  ```

  ⚠️ `CookieSecurePolicy.Always` makes the antiforgery system **throw** on any plain-HTTP request. Behind a TLS-terminating proxy without forwarded headers, that is every request. The middleware survives it — the response goes out intact, without the cookie, and an error is logged — but antiforgery validation will then fail, so fix the forwarded headers rather than relying on the degradation.

- **Validation.** This middleware only *issues* tokens. Rejecting a request is `[ValidateAntiForgeryToken]`, `[AutoValidateAntiforgeryToken]`, or a global filter.

## .NET 11: the automatic CSRF middleware

.NET 11 adds `CsrfProtectionMiddleware`, injected automatically by `WebApplication.CreateBuilder`, which allows same-origin requests on `Sec-Fetch-Site` and rejects cross-site ones. **It does not replace this package.** It validates only endpoints carrying `IAntiforgeryMetadata` with `RequiresValidation = true` — Blazor SSR, minimal APIs binding form data, and MVC actions with an antiforgery attribute. Per the .NET 10→11 migration guide, endpoints that bind JSON, such as a plain `MapPost` or a Web API `[HttpPost]`, have no behavioural change.

So a cookie-authenticated JSON API still needs a token, and still needs a way to get it to the SPA. What you gain on .NET 11 is a free extra layer underneath. Token validation stays authoritative: when the app calls `app.UseAntiforgery()`, the token middleware runs after the CSRF middleware and replaces its verdict.

## Angular version note

Angular's interceptor never attaches the token header to a **cross-origin** request, in any version. It also changed how it recognises same-origin:

| Angular | Rule |
|---|---|
| ≤ 20.0 | Skips *any* absolute URL — including a same-origin one. |
| ≥ 21.0 | Normalises with `new URL(...)` and compares origins, so same-origin absolute URLs now get the header. |

**Use relative API URLs.** That is the only advice that works on every version.

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Abstractions](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Abstractions) - Interfaces for integrating without the implementation
- [MintPlayer.AspNetCore.SpaServices.Prerendering](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Prerendering) - Prerendering support
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration

## License

This project is licensed under the Apache 2.0 License.
