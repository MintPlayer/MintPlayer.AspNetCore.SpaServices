using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using System.Security.Cryptography;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

/// <summary>
/// Behaviour of the cookie itself. See <see cref="XsrfTestHost"/> for why the response feature here
/// is not the stock one, and why it fires callbacks last-in-first-out.
/// </summary>
public class AntiforgeryMiddlewareTests
{
    [Fact]
    public async Task Writes_the_XSRF_TOKEN_cookie_carrying_the_request_token()
    {
        var result = await XsrfTestHost.Run();

        Assert.StartsWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}", result.XsrfCookie);
    }

    [Fact]
    public async Task Writes_the_request_token_rather_than_the_cookie_token()
    {
        // The SPA reads this cookie and echoes it back in a header, so it must be the request token.
        // Sending the cookie token instead would fail validation in a way that is hard to diagnose.
        var result = await XsrfTestHost.Run();

        Assert.DoesNotContain(XsrfTestHost.CookieToken, result.XsrfCookie);
    }

    [Fact]
    public async Task Scopes_the_cookie_to_the_site_root()
    {
        var result = await XsrfTestHost.Run();

        Assert.Contains("path=/", result.XsrfCookie);
    }

    [Fact]
    public async Task Leaves_the_cookie_readable_by_script()
    {
        // HttpOnly is deliberately false: the whole point is for the SPA's JavaScript to read the
        // token and echo it back in a header. Angular's HttpXsrfCookieExtractor reads
        // document.cookie, which cannot see an HttpOnly cookie.
        //
        // Issue #85: this assertion used to stand alone, and a cookie carrying nothing but Path and
        // HttpOnly=false passed it. The Secure and SameSite assertions below exist so that the
        // absence of HttpOnly can no longer be mistaken for the cookie being correctly configured.
        var result = await XsrfTestHost.Run();

        Assert.DoesNotContain("httponly", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Marks_the_cookie_secure_over_https()
    {
        // #85: without Secure the CSRF token travels in clear on any http:// request to the origin.
        var result = await XsrfTestHost.Run(https: true);

        Assert.Contains("secure", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Leaves_the_cookie_insecure_over_plain_http()
    {
        // SameAsRequest, not Always: a browser refuses a Secure cookie from a plain-HTTP origin
        // other than localhost, so Always would break HTTP-only intranet and LAN development.
        var result = await XsrfTestHost.Run(https: false);

        Assert.DoesNotContain("secure", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Marks_the_cookie_secure_over_plain_http_when_the_policy_demands_it()
    {
        var result = await XsrfTestHost.Run(https: false,
            configure: options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);

        Assert.Contains("secure", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Pins_an_explicit_same_site_rather_than_leaving_it_to_the_browser()
    {
        // #85: unset means the browser applies its own default. That default is currently Lax, but
        // it is the browser's choice and not the application's.
        var result = await XsrfTestHost.Run();

        Assert.Contains("samesite=strict", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Honours_a_configured_same_site()
    {
        var result = await XsrfTestHost.Run(configure: options => options.Cookie.SameSite = SameSiteMode.Lax);

        Assert.Contains("samesite=lax", result.XsrfCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Honours_a_configured_cookie_name_and_path()
    {
        var result = await XsrfTestHost.Run(configure: options =>
        {
            options.Cookie.Name = "CUSTOM-XSRF";
            options.Cookie.Path = "/app";
        });

        var cookie = Assert.Single(result.SetCookies, c => c.StartsWith("CUSTOM-XSRF="));
        Assert.Contains("path=/app", cookie);
    }

    [Fact]
    public async Task Calls_the_next_middleware()
    {
        var called = false;
        await XsrfTestHost.Run(next: _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
    }

    [Fact]
    public async Task Writes_no_cookie_until_the_response_starts()
    {
        // The token is issued from an OnStarting callback, so nothing is written while the pipeline
        // is still running. That placement is the package's whole advantage: the token binds to the
        // principal the handler established, so a sign-in response carries a usable token instead of
        // one bound to the anonymous user.
        var result = await XsrfTestHost.Run(fireCallbacks: false);

        Assert.False(result.Context.Response.Headers.ContainsKey("Set-Cookie"));
    }
}

/// <summary>
/// What happens when issuing the token fails. A throw escaping the <c>OnStarting</c> callback costs
/// the entire response under Kestrel, not just the cookie, so every one of these asserts that the
/// response survived.
/// </summary>
public class AntiforgeryMiddlewareFailureTests
{
    [Fact]
    public async Task Writes_no_cookie_when_the_request_token_is_null()
    {
        // Unreachable with the framework's DefaultAntiforgery, whose serializer returns a non-null
        // string; the nullability is an artifact of the internal feature type. Guarded because a
        // consumer can replace IAntiforgery, and because the unguarded ArgumentNullException took
        // the whole response down rather than just the cookie.
        var result = await XsrfTestHost.Run(antiforgery: StubAntiforgery.ReturningNullRequestToken());

        Assert.DoesNotContain(result.SetCookies, c => c.StartsWith("XSRF-TOKEN="));
    }

    [Fact]
    public async Task Logs_an_error_when_the_request_token_is_null()
    {
        var result = await XsrfTestHost.Run(antiforgery: StubAntiforgery.ReturningNullRequestToken());

        Assert.Contains(result.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("null request token"));
    }

    [Theory]
    [InlineData("invalidoperation")]
    [InlineData("cryptographic")]
    public async Task Survives_an_antiforgery_failure_without_losing_the_response(string kind)
    {
        // Kestrel's FireOnStarting catches outside its loop and routes the exception to
        // ReportApplicationError, which makes ProduceEnd emit a bare 500 with every header reset.
        // UseExceptionHandler never sees it. Degrading to "no cookie" is the only sane option.
        var result = await XsrfTestHost.Run(antiforgery: Throwing(kind), status: StatusCodes.Status200OK);

        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
        Assert.DoesNotContain(result.SetCookies, c => c.StartsWith("XSRF-TOKEN="));
    }

    [Theory]
    [InlineData("invalidoperation")]
    [InlineData("cryptographic")]
    public async Task Logs_an_error_carrying_the_antiforgery_failure(string kind)
    {
        var result = await XsrfTestHost.Run(antiforgery: Throwing(kind));

        var entry = Assert.Single(result.Logs, l => l.Level == LogLevel.Error);
        Assert.NotNull(entry.Exception);
        Assert.Contains("no XSRF-TOKEN cookie was written", entry.Message);
    }

    [Fact]
    public async Task Leaves_headers_written_upstream_intact_when_the_mint_fails()
    {
        var result = await XsrfTestHost.Run(
            antiforgery: Throwing("invalidoperation"),
            configureContext: context => context.Response.Headers["X-Upstream"] = "present");

        Assert.Equal("present", result.Context.Response.Headers["X-Upstream"]);
    }

    [Fact]
    public async Task Does_not_abandon_callbacks_registered_before_it_when_the_mint_fails()
    {
        // Kestrel pops OnStarting callbacks LIFO, and the try/catch in FireOnStarting sits outside
        // the loop - so an unguarded throw here skips every callback still on the stack. A callback
        // registered before this middleware is pushed first and therefore pops last, which is
        // exactly the one that used to be lost.
        var ranAfterTheMint = false;

        await XsrfTestHost.Run(
            antiforgery: Throwing("invalidoperation"),
            configureContext: context => context.Response.OnStarting(() =>
            {
                ranAfterTheMint = true;
                return Task.CompletedTask;
            }));

        Assert.True(ranAfterTheMint);
    }

    [Fact]
    public async Task Warns_once_when_the_cookie_goes_out_without_secure_outside_development()
    {
        // The silent half of SameAsRequest: behind a TLS-terminating proxy without
        // UseForwardedHeaders, Request.IsHttps is false and the token ships in clear.
        var result = await XsrfTestHost.Run(https: false, environment: Environments.Production, requests: 3);

        var warning = Assert.Single(result.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("UseForwardedHeaders", warning.Message);
    }

    [Fact]
    public async Task Does_not_warn_about_a_missing_secure_flag_in_development()
    {
        var result = await XsrfTestHost.Run(https: false, environment: Environments.Development);

        Assert.DoesNotContain(result.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Does_not_warn_when_the_cookie_is_secure()
    {
        var result = await XsrfTestHost.Run(https: true, environment: Environments.Production);

        Assert.DoesNotContain(result.Logs, l => l.Level == LogLevel.Warning);
    }

    private static StubAntiforgery Throwing(string kind) => kind switch
    {
        "cryptographic" => StubAntiforgery.Throwing(() => new CryptographicException("key ring unavailable")),
        _ => StubAntiforgery.Throwing(() => new InvalidOperationException(
            "The antiforgery system has the configuration value AntiforgeryOptions.Cookie.SecurePolicy = Always, " +
            "but the current request is not an SSL request.")),
    };
}

/// <summary>
/// The cache headers <c>GetAndStoreTokens</c> writes on its way past, and what survives them.
/// </summary>
public class AntiforgeryMiddlewareCacheHeaderTests
{
    [Fact]
    public async Task Preserves_a_cache_control_set_by_the_application()
    {
        // #85: GetAndStoreTokens stamps no-cache, no-store whenever the response has not started,
        // and inside an OnStarting callback it never has. Because this middleware is registered
        // first and Kestrel pops LIFO, its callback ran last and overwrote everyone.
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = "no-store");

        // Asserting only that no-store survived would pass against the clobber too, since the
        // clobber writes "no-cache, no-store". The absence of no-cache is what distinguishes the
        // application's directive from the antiforgery system's.
        var cacheControl = result.Context.Response.Headers.CacheControl.ToString();
        Assert.Contains("no-store", cacheControl);
        Assert.DoesNotContain("no-cache", cacheControl);
    }

    [Fact]
    public async Task Forces_a_preserved_cache_control_to_be_private()
    {
        // The response carries a Set-Cookie holding this user's token. Restoring a shared-cacheable
        // directive verbatim would let a CDN hand one user's token to the next.
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        var cacheControl = result.Context.Response.Headers.CacheControl.ToString();
        Assert.Contains("private", cacheControl);
        Assert.Contains("max-age=300", cacheControl);
        Assert.DoesNotContain("public", cacheControl);
    }

    [Fact]
    public async Task Leaves_the_no_store_in_place_when_the_application_set_no_cache_policy()
    {
        // Nothing is restored that was not there to begin with, so the SPA's HTML navigation - the
        // response that actually carries a fresh token - is still never cached.
        var result = await XsrfTestHost.Run();

        Assert.Contains("no-store", result.Context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Restores_the_absence_of_pragma()
    {
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = "max-age=60");

        Assert.False(result.Context.Response.Headers.ContainsKey("Pragma"));
    }

    [Fact]
    public async Task Preserves_a_pragma_the_application_set()
    {
        var result = await XsrfTestHost.Run(configureContext: context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.Headers.Pragma = "custom";
        });

        Assert.Equal("custom", result.Context.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Leaves_the_cache_headers_alone_when_the_policy_says_no_store()
    {
        var result = await XsrfTestHost.Run(
            configure: options => options.CacheHeaders = XsrfCacheHeaderPolicy.NoStore,
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        Assert.Equal("no-cache, no-store", result.Context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Leaves_an_already_private_cache_control_untouched()
    {
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = "private, max-age=60");

        Assert.Equal("private, max-age=60", result.Context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    // max-age demands delta-seconds, so these do not parse as a Cache-Control at all. The directives
    // cannot be edited safely, so `private` is prefixed rather than merged.
    [InlineData("max-age=abc", "private, max-age=abc")]
    [InlineData("private, max-age=abc", "private, max-age=abc")]
    public async Task Prefixes_private_onto_an_unparseable_cache_control(string original, string expected)
    {
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = original);

        Assert.Equal(expected, result.Context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Still_writes_the_cookie_when_preserving_cache_headers()
    {
        var result = await XsrfTestHost.Run(
            configureContext: context => context.Response.Headers.CacheControl = "max-age=60");

        Assert.StartsWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}", result.XsrfCookie);
    }
}

/// <summary>
/// Registration, and the configurations rejected at startup rather than left to be diagnosed from a
/// 400 much later.
/// </summary>
public class AntiforgeryExtensionsTests
{
    [Fact]
    public void UseAntiforgeryGenerator_registers_the_middleware()
    {
        var builder = new ApplicationBuilder(XsrfTestHost.BuildServices());

        var result = builder.UseAntiforgeryGenerator();

        Assert.Same(builder, result);
        Assert.NotNull(builder.Build());
    }

    [Fact]
    public async Task A_registered_pipeline_actually_writes_the_cookie()
    {
        // The old registration test only asserted that Build() returned something, so the DI path
        // UseMiddleware takes - including the per-request IAntiforgery resolution - was never run.
        var services = XsrfTestHost.BuildServices();
        var builder = new ApplicationBuilder(services);
        builder.UseAntiforgeryGenerator();
        builder.Run(_ => Task.CompletedTask);
        var pipeline = builder.Build();

        var context = XsrfTestHost.CreateContext(services);
        await pipeline(context);
        await XsrfTestHost.FireCallbacks(context);

        Assert.Contains(context.Response.Headers.SetCookie!, c => c!.StartsWith("XSRF-TOKEN="));
    }

    [Fact]
    public void Rejects_an_http_only_cookie_at_startup()
    {
        var builder = new ApplicationBuilder(XsrfTestHost.BuildServices());

        var ex = Assert.Throws<ArgumentException>(
            () => builder.UseAntiforgeryGenerator(options => options.Cookie.HttpOnly = true));

        Assert.Contains("document.cookie", ex.Message);
    }

    [Fact]
    public void Rejects_same_site_none_without_secure_at_startup()
    {
        var builder = new ApplicationBuilder(XsrfTestHost.BuildServices());

        var ex = Assert.Throws<ArgumentException>(() => builder.UseAntiforgeryGenerator(options =>
        {
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        }));

        Assert.Contains("Secure", ex.Message);
    }

    [Fact]
    public void Accepts_same_site_none_with_secure()
    {
        var builder = new ApplicationBuilder(XsrfTestHost.BuildServices());

        builder.UseAntiforgeryGenerator(options =>
        {
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        Assert.NotNull(builder.Build());
    }

    [Fact]
    public void Rejects_a_nameless_cookie_at_startup()
    {
        // Reached by replacing the whole CookieBuilder, whose Name defaults to null. Assigning ""
        // to the existing builder would be rejected by CookieBuilder.Name itself, which is a
        // different exception from a different type and would pass this test without ever running
        // the package's own validation.
        var builder = new ApplicationBuilder(XsrfTestHost.BuildServices());

        var ex = Assert.Throws<ArgumentException>(
            () => builder.UseAntiforgeryGenerator(options => options.Cookie = new CookieBuilder { HttpOnly = false }));

        Assert.Contains("XsrfOptions.Cookie.Name", ex.Message);
    }
}
