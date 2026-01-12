# MintPlayer.AspNetCore.SpaServices.Xsrf

[![NuGet Version](https://img.shields.io/nuget/v/MintPlayer.AspNetCore.SpaServices.Xsrf.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf)
[![NuGet](https://img.shields.io/nuget/dt/MintPlayer.AspNetCore.SpaServices.Xsrf.svg?style=flat)](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Xsrf)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://opensource.org/licenses/Apache-2.0)

This package provides ASP.NET Core middleware for managing CSRF (Cross-Site Request Forgery) protection in single-page applications.

## Installation

### NuGet Package Manager
```
Install-Package MintPlayer.AspNetCore.SpaServices.Xsrf
```

### .NET CLI
```
dotnet add package MintPlayer.AspNetCore.SpaServices.Xsrf
```

## Features

- Automatic XSRF token generation and validation
- Cookie-based token storage compatible with SPA frameworks
- Seamless integration with Angular's built-in XSRF handling
- Support for custom header and cookie names

## Usage

### 1. Add Services

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-XSRF-TOKEN";
    });
}
```

### 2. Add Middleware

```csharp
public void Configure(IApplicationBuilder app)
{
    app.UseSpaXsrf();

    // ... other middleware
}
```

### 3. Angular Configuration

Angular automatically reads XSRF tokens from cookies named `XSRF-TOKEN` and sends them in the `X-XSRF-TOKEN` header. The middleware is configured to work with these defaults.

If you need custom names, configure both ASP.NET Core and Angular accordingly:

```csharp
// ASP.NET Core
services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CUSTOM-XSRF";
    options.Cookie.Name = "CUSTOM-XSRF-TOKEN";
});
```

```typescript
// Angular (app.module.ts)
@NgModule({
    imports: [
        HttpClientXsrfModule.withOptions({
            cookieName: 'CUSTOM-XSRF-TOKEN',
            headerName: 'X-CUSTOM-XSRF'
        })
    ]
})
```

## Related Packages

- [MintPlayer.AspNetCore.SpaServices](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices) - Core SPA services
- [MintPlayer.AspNetCore.SpaServices.Routing](https://www.nuget.org/packages/MintPlayer.AspNetCore.SpaServices.Routing) - SPA route integration

## License

This project is licensed under the Apache 2.0 License.
