using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.Assertions;
using System.Net;
using System.Text.Encodings.Web;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

/// <summary>
/// Issue #88, part 2, on a real loopback Kestrel with the generator registered <em>above</em>
/// <c>UseRouting()</c> - the placement where <c>.ShortCircuit()</c> cannot prevent the mint, and the
/// one the demo uses. Every skipped endpoint here sets <c>public, max-age=300</c>.
/// </summary>
/// <remarks>
/// Real Kestrel rather than a simulated feature collection because the claims are about the server's
/// pipeline: the endpoint is only known once routing has run, the callback fires when Kestrel starts
/// the response, and the rate limiter and authorization must still see the request.
/// </remarks>
public class XsrfSkipTokenPipelineTests(XsrfSkipTokenPipelineTests.Server server) : IClassFixture<XsrfSkipTokenPipelineTests.Server>
{
    private const string PublicCacheControl = "public, max-age=300";

    private static Task PublicResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = PublicCacheControl;
        return context.Response.WriteAsync("ok");
    }

    /// <summary>The raw header, not <c>HttpClient</c>'s parsed and re-ordered rendering of it.</summary>
    private static string CacheControl(HttpResponseMessage response)
        => string.Join(", ", response.Headers.GetValues("Cache-Control"));

    [Theory]
    [InlineData("/badge")]
    [InlineData("/public/feed")]
    [InlineData("/skipped-controller/index")]
    [InlineData("/controller/skipped-action")]
    public async Task A_skipped_endpoint_is_left_exactly_as_the_application_produced_it(string path)
    {
        using var response = await server.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        CacheControl(response).Should().Be(PublicCacheControl);
        response.Headers.Contains("Pragma").Should().BeFalse();
        // The mint's side effect, documented as lost on a skipped endpoint.
        response.Headers.Contains("X-Frame-Options").Should().BeFalse();
    }

    [Theory]
    [InlineData("/minted")]
    [InlineData("/controller/plain-action")]
    // Re-enabled inside a skipped group / controller: the most specific metadata wins.
    [InlineData("/public/page")]
    [InlineData("/skipped-controller/page")]
    public async Task A_neighbouring_endpoint_still_mints(string path)
    {
        using var response = await server.Client.GetAsync(path);

        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("XSRF-TOKEN="));
        CacheControl(response).Should().Be("max-age=300, private");
        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
    }

    [Fact]
    public async Task A_request_that_matches_no_endpoint_still_mints()
    {
        using var response = await server.Client.GetAsync("/nowhere");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("XSRF-TOKEN="));
    }

    [Fact]
    public async Task The_rate_limiter_still_runs_for_a_skipped_endpoint()
    {
        // The contrast with ShortCircuit(), which takes the endpoint out of UseRateLimiter.
        using var first = await server.Client.GetAsync("/limited");
        using var second = await server.Client.GetAsync("/limited");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        first.Headers.Contains("Set-Cookie").Should().BeFalse();
        second.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Authorization_still_runs_for_a_skipped_endpoint()
    {
        using var response = await server.Client.GetAsync("/secured");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task The_framework_logs_no_override_warning_for_a_preserved_policy()
    {
        // Part 1 on the real server: /minted declares public caching and goes through the full mint.
        using var response = await server.Client.GetAsync("/minted");

        CacheControl(response).Should().Be("max-age=300, private");
        server.Logs.Entries.Should().NotContain(l => l.EventId.Id == XsrfTestHost.CacheHeadersOverriddenEventId);
    }

    public sealed class Server : IAsyncLifetime
    {
        private WebApplication? app;

        public HttpClient Client { get; private set; } = null!;

        internal XsrfTestHost.RecordingLoggerProvider Logs { get; } = new();

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders().AddProvider(Logs).SetMinimumLevel(LogLevel.Information);

            builder.Services.AddAntiforgery();
            // In-memory keys, as in XsrfTestHost.BuildServices: no key files on the runner, and no
            // "No XML encryptor configured" warning on Linux.
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddControllers().AddApplicationPart(typeof(XsrfSkipTokenPipelineTests).Assembly);
            builder.Services.AddRateLimiter(options =>
            {
                // The framework's default rejection is 503.
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddFixedWindowLimiter("one", window =>
                {
                    window.PermitLimit = 1;
                    window.Window = TimeSpan.FromHours(1);
                    window.QueueLimit = 0;
                });
            });
            builder.Services.AddAuthentication(AnonymousOnlyHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AnonymousOnlyHandler>(AnonymousOnlyHandler.SchemeName, null);
            builder.Services.AddAuthorization();

            app = builder.Build();

            // Above UseRouting: the endpoint is not yet known when the middleware runs, only when the
            // response starts.
            app.UseAntiforgeryGenerator();
            app.UseRouting();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/badge", PublicResponse).SkipXsrfToken();
            var group = app.MapGroup("/public").SkipXsrfToken();
            group.MapGet("/feed", PublicResponse);
            group.MapGet("/page", PublicResponse).SkipXsrfToken(skip: false);
            app.MapGet("/minted", PublicResponse);
            app.MapGet("/limited", PublicResponse).SkipXsrfToken().RequireRateLimiting("one");
            app.MapGet("/secured", PublicResponse).SkipXsrfToken().RequireAuthorization();
            app.MapControllers();

            await app.StartAsync();

            Client = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>Authenticates nobody, so <c>RequireAuthorization</c> challenges with a 401.</summary>
    private sealed class AnonymousOnlyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "AnonymousOnly";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}

[SkipXsrfToken]
[Route("skipped-controller")]
public sealed class SkippedXsrfController : ControllerBase
{
    [HttpGet("index")]
    public IActionResult Index()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Content("ok");
    }

    /// <summary>The one exception in an otherwise skipped controller.</summary>
    [SkipXsrfToken(false)]
    [HttpGet("page")]
    public IActionResult Page()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Content("ok");
    }
}

[Route("controller")]
public sealed class PartlySkippedXsrfController : ControllerBase
{
    [SkipXsrfToken]
    [HttpGet("skipped-action")]
    public IActionResult Skipped()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Content("ok");
    }

    [HttpGet("plain-action")]
    public IActionResult Plain()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Content("ok");
    }
}
