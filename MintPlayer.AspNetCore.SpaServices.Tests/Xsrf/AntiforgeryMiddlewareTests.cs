using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

public class AntiforgeryMiddlewareTests
{
    private const string RequestToken = "the-request-token";
    private const string CookieToken = "the-cookie-token";

    [Fact]
    public async Task Writes_the_XSRF_TOKEN_cookie_carrying_the_request_token()
    {
        var context = await InvokeMiddleware();

        var setCookie = Assert.Single(context.Response.Headers.SetCookie!);
        Assert.StartsWith($"XSRF-TOKEN={RequestToken}", setCookie);
    }

    [Fact]
    public async Task Writes_the_request_token_rather_than_the_cookie_token()
    {
        // The SPA reads this cookie and echoes it back in a header, so it must be the request token.
        // Sending the cookie token instead would fail validation in a way that is hard to diagnose.
        var context = await InvokeMiddleware();

        var setCookie = Assert.Single(context.Response.Headers.SetCookie!);
        Assert.DoesNotContain(CookieToken, setCookie);
    }

    [Fact]
    public async Task Scopes_the_cookie_to_the_site_root()
    {
        var context = await InvokeMiddleware();

        Assert.Contains("path=/", Assert.Single(context.Response.Headers.SetCookie!));
    }

    [Fact]
    public async Task Leaves_the_cookie_readable_by_script()
    {
        // HttpOnly is deliberately false: the whole point is for the SPA's JavaScript to read the
        // token and echo it back in a header.
        var context = await InvokeMiddleware();

        Assert.DoesNotContain("httponly", Assert.Single(context.Response.Headers.SetCookie!).ToLowerInvariant());
    }

    [Fact]
    public async Task Calls_the_next_middleware()
    {
        var called = false;
        await InvokeMiddleware(next: _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
    }

    [Fact]
    public async Task Writes_no_cookie_until_the_response_starts()
    {
        // The token is issued from an OnStarting callback, so nothing is written while the pipeline
        // is still running. This is what lets a later middleware change the response first.
        var context = new DefaultHttpContext(new TestFeatureCollection());
        context.RequestServices = BuildServices();

        var middleware = new Antiforgery(_ => Task.CompletedTask, new StubAntiforgery());
        await middleware.Invoke(context);

        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    private static async Task<DefaultHttpContext> InvokeMiddleware(RequestDelegate? next = null)
    {
        var features = new TestFeatureCollection();
        var context = new DefaultHttpContext(features) { RequestServices = BuildServices() };

        var middleware = new Antiforgery(next ?? (_ => Task.CompletedTask), new StubAntiforgery());
        await middleware.Invoke(context);
        await features.ResponseFeature.FireOnStartingAsync();

        return context;
    }

    private static ServiceProvider BuildServices()
        => new ServiceCollection().AddLogging().AddAntiforgery().BuildServiceProvider();

    /// <summary>
    /// A stock <see cref="FeatureCollection"/>'s response feature accepts <c>OnStarting</c> callbacks
    /// and silently discards them, which would make every assertion here pass vacuously with no
    /// cookie ever written. This substitutes a feature that actually stores and runs them.
    /// </summary>
    private sealed class TestFeatureCollection : FeatureCollection
    {
        public RunnableResponseFeature ResponseFeature { get; } = new();

        public TestFeatureCollection()
        {
            Set<IHttpRequestFeature>(new HttpRequestFeature());
            Set<IHttpResponseFeature>(ResponseFeature);
            Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        }
    }

    private sealed class RunnableResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state)
            => callbacks.Add((callback, state));

        public async Task FireOnStartingAsync()
        {
            foreach (var (callback, state) in callbacks)
                await callback(state);
        }
    }

    private sealed class StubAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => GetTokens(httpContext);

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext)
            => new(RequestToken, CookieToken, "__RequestVerificationToken", "X-XSRF-TOKEN");

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext) { }

        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    }
}

public class AntiforgeryExtensionsTests
{
    [Fact]
    public void UseAntiforgeryGenerator_registers_the_middleware()
    {
        var services = new ServiceCollection().AddLogging().AddAntiforgery().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);

        var result = builder.UseAntiforgeryGenerator();

        Assert.Same(builder, result);
        Assert.NotNull(builder.Build());
    }
}
