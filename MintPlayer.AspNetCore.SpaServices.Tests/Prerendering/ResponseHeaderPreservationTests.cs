using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// Covers <see href="https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81">issue #81</see>:
/// <c>ServePrerenderResult</c> opened with <c>context.Response.Clear()</c>, which emptied the entire
/// response header dictionary and reset the status code to 200 - discarding everything every
/// middleware upstream of <c>UseSpaPrerendering</c> had written, on exactly the responses that get
/// prerendered.
/// </summary>
/// <remarks>
/// <para>
/// The contract these tests pin: prerendering removes the headers that describe the <em>captured
/// template</em> (representation metadata plus framing) and preserves everything else. Nothing
/// enumerates what is kept - a header survives because it is not in the drop-set, which is why an
/// unknown application header survives too.
/// </para>
/// <para>
/// Several tests assert a header is <em>absent</em>. Those passed before the fix as well, because
/// <c>Clear()</c> removed everything indiscriminately; they are regression guards for the new
/// targeted removal, not evidence that the bug was reproduced. The tests that actually went red are
/// the preservation ones and the whole status group.
/// </para>
/// </remarks>
public class ResponseHeaderPreservationTests
{
    private const string Template = "<!doctype html><html><head></head><body><app-root></app-root></body></html>";

    /// <summary>
    /// The default harness host is <c>localhost</c>, which is in <c>HstsOptions.ExcludedHosts</c> by
    /// default - the framework would skip the header for a reason that has nothing to do with this
    /// bug, and the test would pass while proving nothing.
    /// </summary>
    private const string PublicHost = "example.com";

    private static PrerenderingHarness.RecordingNodeServices Node(string? html = null)
        => new() { Html = html ?? "<html><body>prerendered</body></html>" };

    /// <summary>
    /// Mirrors the builder in <see cref="UseSpaPrerenderingGuardTests"/>: the option validation under
    /// test runs before the builder is dereferenced, so touching it means the test escaped into the
    /// middleware body.
    /// </summary>
    private sealed class UnusableSpaBuilder(Core.SpaOptions options) : Abstractions.ISpaBuilder
    {
        public IApplicationBuilder ApplicationBuilder => throw new InvalidOperationException("The test reached the middleware body.");

        public Abstractions.ISpaOptions Options { get; } = options;
    }

    /// <summary>
    /// Runs one request all the way through a successful prerender. <paramref name="upstream"/> is
    /// registered before the prerendering middleware, which is where a real app writes the headers
    /// this issue is about.
    /// </summary>
    private static Task<PrerenderingHarness.Result> Prerender(
        Action<HttpContext>? upstream = null,
        Action<SpaPrerenderingOptions>? configureOptions = null,
        PrerenderingHarness.RecordingNodeServices? node = null,
        RequestDelegate? innerPipeline = null,
        int statusCodeFromOnSupplyData = StatusCodes.Status200OK,
        Action<HttpContext>? onSupplyData = null,
        string? locationFromOnSupplyData = "/redirected")
        => PrerenderingHarness.Run(
            innerPipeline ?? PrerenderingHarness.HtmlPage(Template),
            recordingNodeServices: node ?? Node(),
            statusCodeFromOnSupplyData: statusCodeFromOnSupplyData,
            configureOptions: configureOptions,
            onSupplyData: onSupplyData,
            locationFromOnSupplyData: locationFromOnSupplyData,
            configureContext: context => context.Request.Host = new HostString(PublicHost),
            configureUpstream: upstream is null
                ? null
                : app => app.Use(async (context, next) =>
                {
                    // Eagerly, on the way in - the timing that made these headers vulnerable.
                    upstream(context);
                    await next();
                }));

    // ---------------------------------------------------------------------------------------
    // Headers that must survive
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline test. Microsoft's own <c>HstsMiddleware</c>, unmodified, writes
    /// <c>Strict-Transport-Security</c> eagerly before calling the rest of the pipeline and never
    /// re-applies it - so <c>Clear()</c> silently stripped it from every server-rendered navigation,
    /// which is precisely where a browser sets and refreshes its HSTS pin.
    /// </summary>
    [Fact]
    public async Task Preserves_the_hsts_header_written_by_the_frameworks_own_middleware()
    {
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(Template),
            recordingNodeServices: Node(),
            statusCodeFromOnSupplyData: StatusCodes.Status200OK,
            configureContext: context => context.Request.Host = new HostString(PublicHost),
            configureServices: services => services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365)),
            configureUpstream: app => app.UseHsts());

        Assert.Equal(
            "max-age=31536000",
            result.Context.Response.Headers.StrictTransportSecurity);
    }

    [Fact]
    public async Task Preserves_security_headers_set_eagerly_upstream()
    {
        var result = await Prerender(upstream: context =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
        });

        Assert.Equal("default-src 'self'", result.Context.Response.Headers.ContentSecurityPolicy);
        Assert.Equal("DENY", result.Context.Response.Headers.XFrameOptions);
        Assert.Equal("no-referrer", result.Context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("nosniff", result.Context.Response.Headers.XContentTypeOptions);
    }

    /// <summary>
    /// The reporter's other case. Caching headers are preserved by default because
    /// <c>StaticFileMiddleware</c> sets none of its own, so the value present is the one upstream
    /// middleware intended.
    /// </summary>
    [Fact]
    public async Task Preserves_an_upstream_cache_control_policy()
    {
        var result = await Prerender(upstream: context =>
            context.Response.Headers.CacheControl = "no-store");

        Assert.Equal("no-store", result.Context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Preserves_a_set_cookie_written_upstream()
    {
        var result = await Prerender(upstream: context =>
            context.Response.Headers.SetCookie = "session=abc; Path=/; HttpOnly");

        Assert.Equal("session=abc; Path=/; HttpOnly", result.Context.Response.Headers.SetCookie);
    }

    /// <summary>
    /// Nothing enumerates the headers that are kept, so a header this library has never heard of
    /// has to survive for the same reason a security header does.
    /// </summary>
    [Fact]
    public async Task Preserves_an_unknown_application_header()
    {
        var result = await Prerender(upstream: context =>
            context.Response.Headers["X-Correlation-Id"] = "d34db33f");

        Assert.Equal("d34db33f", result.Context.Response.Headers["X-Correlation-Id"]);
    }

    /// <summary>
    /// A preserved header keeps exactly the value it had - it is never removed and re-added, so it
    /// cannot end up duplicated.
    /// </summary>
    /// <remarks>
    /// This is what ruled out the snapshot-and-restore approach proposed in the issue. Restoring a
    /// captured value cannot be made safe, because <c>Clear()</c> does not remove <c>OnStarting</c>
    /// callbacks and <c>ResponseCompressionBody</c> <em>concatenates</em> onto <c>Vary</c> and
    /// <c>Content-Encoding</c> rather than assigning - so a restored value would be appended to a
    /// second time, yielding <c>gzip, gzip</c> and an undecodable body. Leaving the header untouched
    /// removes the hazard by construction rather than by careful bookkeeping.
    /// </remarks>
    [Fact]
    public async Task Preserves_a_header_exactly_once()
    {
        var result = await Prerender(upstream: context =>
            context.Response.Headers.Vary = HeaderNames.AcceptEncoding);

        Assert.Equal(HeaderNames.AcceptEncoding, Assert.Single(result.Context.Response.Headers.Vary!));
    }

    [Fact]
    public async Task Preserves_upstream_headers_on_the_redirect_branch()
    {
        var result = await Prerender(
            upstream: context => context.Response.Headers.StrictTransportSecurity = "max-age=100",
            node: new PrerenderingHarness.RecordingNodeServices { RedirectUrl = "/elsewhere" });

        Assert.Equal("/elsewhere", result.Context.Response.Headers.Location);
        Assert.Equal("max-age=100", result.Context.Response.Headers.StrictTransportSecurity);
    }

    // ---------------------------------------------------------------------------------------
    // Headers that describe the captured template and must not survive
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The most dangerous one to get wrong. The static file's validator on a per-user rendered body
    /// makes later conditional requests return 304, so shared caches would serve one user's page to
    /// everyone else.
    /// </summary>
    [Fact]
    public async Task Drops_the_validators_of_the_captured_template()
    {
        var result = await Prerender(innerPipeline: async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.Headers.ETag = "\"abc123\"";
            context.Response.Headers.LastModified = "Wed, 21 Oct 2015 07:28:00 GMT";
            context.Response.Headers.AcceptRanges = "bytes";
            var bytes = Encoding.UTF8.GetBytes(Template);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
        });

        Assert.False(result.Context.Response.Headers.ContainsKey(HeaderNames.ETag));
        Assert.False(result.Context.Response.Headers.ContainsKey(HeaderNames.LastModified));
        Assert.False(result.Context.Response.Headers.ContainsKey(HeaderNames.AcceptRanges));
    }

    /// <summary>
    /// The template's byte count is not the rendered page's. Emitting a stale one truncates the
    /// response or, on a keep-alive connection, desynchronises it.
    /// </summary>
    [Fact]
    public async Task Never_emits_the_captured_templates_content_length()
    {
        var rendered = "<html><body>a longer prerendered document than the template was</body></html>";

        var result = await Prerender(node: Node(rendered));

        var templateLength = Encoding.UTF8.GetByteCount(Template);
        var contentLength = result.Context.Response.ContentLength;

        Assert.True(
            contentLength is null || contentLength == Encoding.UTF8.GetByteCount(rendered),
            $"Content-Length was {contentLength}; expected absent or {Encoding.UTF8.GetByteCount(rendered)}, never the template's {templateLength}.");
    }

    [Fact]
    public async Task Replaces_the_captured_content_type()
    {
        var result = await Prerender(innerPipeline: async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=iso-8859-1";
            await context.Response.WriteAsync(Template);
        });

        Assert.Equal("text/html", result.Context.Response.ContentType);
    }

    // ---------------------------------------------------------------------------------------
    // Status codes: assigned in OnSupplyData, with no OnStarting callback
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline test for the second defect. Before the fix a consumer had to assign the status
    /// from inside a <c>Response.OnStarting</c> callback so it survived <c>Clear()</c> - and because
    /// a deferred status is invisible to the prerender gate, they also had to call
    /// <c>SkipPrerendering()</c>, which meant no rendered page at all. A rendered "not found" page
    /// returned with a 404 was not expressible.
    /// </summary>
    [Fact]
    public async Task Renders_the_page_and_keeps_a_404_assigned_in_on_supply_data()
    {
        var node = Node("<html><body>not found</body></html>");

        var result = await Prerender(node: node, statusCodeFromOnSupplyData: StatusCodes.Status404NotFound);

        Assert.True(node.WasInvoked, "The page should still be prerendered - a 404 needs a rendered body.");
        Assert.Equal(StatusCodes.Status404NotFound, result.Context.Response.StatusCode);
        Assert.Contains("not found", Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Fact]
    public async Task Renders_the_page_and_keeps_a_403_assigned_in_on_supply_data()
    {
        var node = Node("<html><body>forbidden</body></html>");

        var result = await Prerender(node: node, statusCodeFromOnSupplyData: StatusCodes.Status403Forbidden);

        Assert.True(node.WasInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    /// <summary>A redirect has no body worth rendering, so the gate passes it straight through.</summary>
    [Fact]
    public async Task Skips_prerendering_for_a_redirect_assigned_in_on_supply_data()
    {
        var node = Node();

        var result = await Prerender(
            node: node,
            statusCodeFromOnSupplyData: StatusCodes.Status302Found,
            onSupplyData: context => context.Response.Headers.Location = "/somewhere-else");

        Assert.False(node.WasInvoked, "A redirect should not be prerendered.");
        Assert.Equal(StatusCodes.Status302Found, result.Context.Response.StatusCode);
        Assert.Equal("/somewhere-else", result.Context.Response.Headers.Location);
    }

    /// <summary>
    /// A 3xx without <c>Location</c> is not a redirect. 300 Multiple Choices is the realistic case,
    /// and it can carry a body, so it is rendered - deliberately, so that the redirect rule stays a
    /// rule about redirects.
    /// </summary>
    [Fact]
    public async Task Prerenders_a_3xx_that_carries_no_location()
    {
        var node = Node();

        var result = await Prerender(
            node: node,
            statusCodeFromOnSupplyData: StatusCodes.Status300MultipleChoices,
            locationFromOnSupplyData: null);

        Assert.True(node.WasInvoked);
        Assert.Equal(StatusCodes.Status300MultipleChoices, result.Context.Response.StatusCode);
    }

    /// <summary>
    /// A status the render result carries wins over one the server assigned - node only sets it when
    /// the boot module deliberately returns one, so it is an explicit override rather than a default.
    /// </summary>
    [Fact]
    public async Task The_render_results_status_wins_over_one_assigned_in_on_supply_data()
    {
        var node = new PrerenderingHarness.RecordingNodeServices { StatusCode = StatusCodes.Status410Gone };

        var result = await Prerender(node: node, statusCodeFromOnSupplyData: StatusCodes.Status404NotFound);

        Assert.Equal(StatusCodes.Status410Gone, result.Context.Response.StatusCode);
    }

    /// <summary>
    /// The pattern consumers had to use before the fix still works. It is obsolete, not broken -
    /// nothing removes <c>OnStarting</c> support, so a consumer can migrate at their own pace.
    /// </summary>
    [Fact]
    public async Task Still_honours_a_status_assigned_from_an_on_starting_callback()
    {
        var result = await Prerender(
            statusCodeFromOnSupplyData: StatusCodes.Status200OK,
            onSupplyData: context => context.Response.OnStarting(() =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }));

        Assert.Equal(StatusCodes.Status404NotFound, result.Context.Response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Statuses that cannot carry a body
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status205ResetContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task Writes_no_body_for_a_status_that_cannot_carry_one(int statusCode)
    {
        var node = Node();

        var result = await Prerender(node: node, statusCodeFromOnSupplyData: statusCode);

        Assert.False(node.WasInvoked, "A body-less status should not be prerendered.");
        Assert.Equal(statusCode, result.Context.Response.StatusCode);
        Assert.Empty(result.ClientBody.ToArray());
    }

    /// <summary>
    /// A pre-existing defect, independent of the header work. <c>PassThroughAsync</c> copies the
    /// captured buffer unconditionally, so a 304 emitted the entire captured <c>index.html</c> as its
    /// body. <c>CanHaveResponseBody</c> already existed and already knew about 204/205/304 and HEAD -
    /// it just guarded the <c>Content-Length</c> reconciliation and not the copy. HEAD escaped only
    /// by accident, because a HEAD leaves the buffer empty.
    /// </summary>
    [Fact]
    public async Task Pass_through_writes_no_body_when_the_status_forbids_one()
    {
        var result = await Prerender(
            statusCodeFromOnSupplyData: StatusCodes.Status304NotModified,
            node: Node());

        Assert.Empty(result.ClientBody.ToArray());
    }

    // ---------------------------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Preserve_response_headers_rescues_a_header_from_the_drop_set()
    {
        var result = await Prerender(
            configureOptions: options => options.PreserveResponseHeaders.Add(HeaderNames.ETag),
            innerPipeline: async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                context.Response.Headers.ETag = "\"kept\"";
                await context.Response.WriteAsync(Template);
            });

        Assert.Equal("\"kept\"", result.Context.Response.Headers.ETag);
    }

    [Fact]
    public async Task Drop_response_headers_removes_a_header_that_would_otherwise_be_kept()
    {
        var result = await Prerender(
            upstream: context => context.Response.Headers["X-Internal"] = "leaked",
            configureOptions: options => options.DropResponseHeaders.Add("X-Internal"));

        Assert.False(result.Context.Response.Headers.ContainsKey("X-Internal"));
    }

    /// <summary>Header names are case-insensitive per RFC 9110 §5.1, and so are both collections.</summary>
    [Fact]
    public async Task Matches_configured_header_names_case_insensitively()
    {
        var result = await Prerender(
            upstream: context => context.Response.Headers["X-Internal"] = "leaked",
            configureOptions: options => options.DropResponseHeaders.Add("x-INTERNAL"));

        Assert.False(result.Context.Response.Headers.ContainsKey("X-Internal"));
    }

    /// <summary>
    /// Framing headers are correctness, not policy: emitting the template's length alongside a
    /// different body corrupts the response. Rejected when the middleware is registered, so the
    /// mistake surfaces at startup rather than as a corrupted response under load.
    /// </summary>
    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Content-Range")]
    public void Rejects_a_framing_header_in_preserve_response_headers(string headerName)
    {
        var builder = new UnusableSpaBuilder(new Core.SpaOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => builder.UseSpaPrerendering(options =>
        {
            options.BootModulePath = "dist/server/main.js";
            options.PreserveResponseHeaders.Add(headerName);
        }));

        Assert.Contains(headerName, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(SpaPrerenderingOptions.PreserveResponseHeaders), exception.Message);
    }

    /// <summary>
    /// A name in both collections contradicts itself. Picking a precedence would hide the mistake,
    /// so it is rejected instead.
    /// </summary>
    [Fact]
    public void Rejects_a_header_named_in_both_collections()
    {
        var builder = new UnusableSpaBuilder(new Core.SpaOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => builder.UseSpaPrerendering(options =>
        {
            options.BootModulePath = "dist/server/main.js";
            options.PreserveResponseHeaders.Add("X-Confused");
            options.DropResponseHeaders.Add("x-confused");
        }));

        Assert.Contains("X-Confused", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The drop-set has to be discoverable, or consumers are adjusting something invisible.</summary>
    [Fact]
    public void Exposes_the_default_drop_set()
    {
        Assert.Contains(HeaderNames.ETag, SpaPrerenderingOptions.DefaultDroppedResponseHeaders);
        Assert.Contains(HeaderNames.ContentLength, SpaPrerenderingOptions.DefaultDroppedResponseHeaders);

        // Caching headers are deliberately absent - see decision 4 in the PRD.
        Assert.DoesNotContain(HeaderNames.CacheControl, SpaPrerenderingOptions.DefaultDroppedResponseHeaders);
        Assert.DoesNotContain(HeaderNames.Vary, SpaPrerenderingOptions.DefaultDroppedResponseHeaders);
    }
}
