using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// Reaches the private static helpers of <see cref="SpaPrerenderingExtensions"/> by reflection.
/// The helpers are deliberately not widened in the library, so the tests bind them once here and
/// expose them as ordinary typed methods to keep the test bodies readable.
/// </summary>
internal static class SpaPrerenderingReflection
{
    private static MethodInfo Method(string name)
        => typeof(SpaPrerenderingExtensions).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"SpaPrerenderingExtensions.{name} no longer exists.");

    public static bool IsHtmlContentType(string? contentType)
        => (bool)Method(nameof(IsHtmlContentType)).Invoke(null, [contentType])!;

    public static void RemoveConditionalRequestHeaders(HttpRequest request)
        => Method(nameof(RemoveConditionalRequestHeaders)).Invoke(null, [request]);

    public static string? GetAndRemoveAcceptEncodingHeader(HttpRequest request)
        => (string?)Method(nameof(GetAndRemoveAcceptEncodingHeader)).Invoke(null, [request]);

    public static (string AbsoluteUrl, string PathAndQuery) GetUnencodedUrlAndPathQuery(HttpContext httpContext)
        => ((string, string))Method(nameof(GetUnencodedUrlAndPathQuery)).Invoke(null, [httpContext])!;

    public static Task ServePrerenderResult(
        HttpContext context,
        RenderToStringResult renderResult,
        IReadOnlyCollection<string>? headersToDrop = null)
        // The target is an async method, so a failure surfaces on the returned task rather than as
        // a TargetInvocationException from Invoke itself.
        => (Task)Method(nameof(ServePrerenderResult)).Invoke(
            null,
            [context, renderResult, headersToDrop ?? SpaPrerenderingOptions.DefaultDroppedResponseHeaders])!;
}

internal static class PrerenderingTestContext
{
    /// <summary>
    /// A <see cref="DefaultHttpContext"/> over a bare feature collection has no request or response
    /// features at all, so every feature the code under test touches has to be supplied explicitly.
    /// </summary>
    public static DefaultHttpContext Create(string? rawTarget = null, MemoryStream? responseBody = null)
    {
        var features = new FeatureCollection();

        // HttpRequestFeature.Method defaults to string.Empty, which is not GET - and the
        // prerendering middleware only prerenders GET requests, so without this every test would
        // short-circuit before reaching the code under test.
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { RawTarget = rawTarget!, Method = HttpMethods.Get });
        features.Set<IHttpResponseFeature>(new CallbackFiringResponseFeature());

        // A response body feature is always required: HttpResponse.Clear() reads Response.Body,
        // which throws a NullReferenceException without one - even on the redirect path that
        // never writes anything.
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody ?? new MemoryStream()));

        return new DefaultHttpContext(features);
    }

    /// <summary>
    /// <see cref="HttpResponseFeature.OnStarting"/> is a no-op stub, so callbacks registered against
    /// a bare feature collection are silently dropped and any test about deferred header or status
    /// writes passes or fails for the wrong reason.
    /// </summary>
    /// <remarks>
    /// Kestrel fires these at the first write, in reverse registration order, and the response is
    /// only marked started afterwards - so a callback may still set a status or a header. This fires
    /// them once, on the first write or when the harness flushes, and preserves the LIFO order.
    /// </remarks>
    internal sealed class CallbackFiringResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _onStarting = new();
        private bool _fired;

        public override void OnStarting(Func<object, Task> callback, object state)
            => _onStarting.Push((callback, state));

        /// <summary>Runs the registered callbacks, once, newest first.</summary>
        public async Task FireOnStartingAsync()
        {
            if (_fired)
            {
                return;
            }

            _fired = true;

            while (_onStarting.Count > 0)
            {
                var (callback, state) = _onStarting.Pop();
                await callback(state);
            }
        }
    }
}

public class IsHtmlContentTypeTests
{
    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("text/html;charset=utf-8")]
    public void Accepts_html_content_types(string contentType)
    {
        Assert.True(SpaPrerenderingReflection.IsHtmlContentType(contentType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/plain")]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/htmlx")]
    public void Rejects_everything_else(string? contentType)
    {
        Assert.False(SpaPrerenderingReflection.IsHtmlContentType(contentType));
    }

    [Theory]
    [InlineData("TEXT/HTML")]
    [InlineData("Text/Html; charset=utf-8")]
    [InlineData(" text/html")]
    public void Accepts_content_types_that_differ_only_in_case_or_whitespace(string contentType)
    {
        // Media types are case-insensitive (RFC 9110 8.3.1). These used to be compared Ordinal
        // against a lowercase literal, so a validly-cased-differently response silently skipped
        // prerendering and was passed through unrendered.
        Assert.True(SpaPrerenderingReflection.IsHtmlContentType(contentType));
    }

    [Fact]
    public void Accepts_optional_whitespace_before_the_parameter_separator()
    {
        // "text/html ; charset=utf-8" is legal per the grammar (OWS is allowed before the ';'), but
        // used to match neither the exact string nor the "text/html;" prefix.
        Assert.True(SpaPrerenderingReflection.IsHtmlContentType("text/html ; charset=utf-8"));
    }
}

public class RemoveConditionalRequestHeadersTests
{
    [Fact]
    public void Removes_every_conditional_header()
    {
        var context = PrerenderingTestContext.Create();
        var headers = context.Request.Headers;
        headers[HeaderNames.IfMatch] = "\"etag\"";
        headers[HeaderNames.IfModifiedSince] = "Wed, 21 Oct 2015 07:28:00 GMT";
        headers[HeaderNames.IfNoneMatch] = "\"etag\"";
        headers[HeaderNames.IfUnmodifiedSince] = "Wed, 21 Oct 2015 07:28:00 GMT";
        headers[HeaderNames.IfRange] = "\"etag\"";

        // Range goes with them: the capture has to be the whole document, and a Range request makes
        // StaticFileMiddleware answer 206 with a slice of it (issue #80).
        headers[HeaderNames.Range] = "bytes=0-0";
        headers[HeaderNames.Accept] = "text/html";

        SpaPrerenderingReflection.RemoveConditionalRequestHeaders(context.Request);

        Assert.False(headers.ContainsKey(HeaderNames.IfMatch));
        Assert.False(headers.ContainsKey(HeaderNames.IfModifiedSince));
        Assert.False(headers.ContainsKey(HeaderNames.IfNoneMatch));
        Assert.False(headers.ContainsKey(HeaderNames.IfUnmodifiedSince));
        Assert.False(headers.ContainsKey(HeaderNames.IfRange));
        Assert.False(headers.ContainsKey(HeaderNames.Range));
        Assert.Equal("text/html", headers[HeaderNames.Accept].ToString());
    }

    [Fact]
    public void Is_a_no_op_when_no_conditional_headers_are_present()
    {
        var context = PrerenderingTestContext.Create();
        context.Request.Headers[HeaderNames.Accept] = "text/html";

        SpaPrerenderingReflection.RemoveConditionalRequestHeaders(context.Request);

        Assert.Single(context.Request.Headers);
    }
}

public class GetAndRemoveAcceptEncodingHeaderTests
{
    [Fact]
    public void Returns_the_value_and_removes_the_header()
    {
        var context = PrerenderingTestContext.Create();
        context.Request.Headers[HeaderNames.AcceptEncoding] = "gzip";

        var value = SpaPrerenderingReflection.GetAndRemoveAcceptEncodingHeader(context.Request);

        Assert.Equal("gzip", value);
        Assert.False(context.Request.Headers.ContainsKey(HeaderNames.AcceptEncoding));
    }

    [Fact]
    public void Returns_null_when_the_header_is_absent()
    {
        var context = PrerenderingTestContext.Create();

        Assert.Null(SpaPrerenderingReflection.GetAndRemoveAcceptEncodingHeader(context.Request));
    }

    [Fact]
    public void Joins_a_multi_valued_header_into_one_string()
    {
        // The value is flattened by the StringValues-to-string conversion, so restoring it later
        // in the middleware writes back a single combined header value rather than two.
        var context = PrerenderingTestContext.Create();
        context.Request.Headers[HeaderNames.AcceptEncoding] = new Microsoft.Extensions.Primitives.StringValues(["gzip", "br"]);

        Assert.Equal("gzip,br", SpaPrerenderingReflection.GetAndRemoveAcceptEncodingHeader(context.Request));
    }

    [Fact]
    public void Returns_an_empty_string_rather_than_null_for_a_present_but_empty_header()
    {
        // Pins current behaviour: the middleware only restores the header when the returned value
        // is non-empty, so an explicitly empty Accept-Encoding is silently dropped from the request.
        var context = PrerenderingTestContext.Create();
        context.Request.Headers[HeaderNames.AcceptEncoding] = string.Empty;

        Assert.Equal(string.Empty, SpaPrerenderingReflection.GetAndRemoveAcceptEncodingHeader(context.Request));
    }
}

public class GetUnencodedUrlAndPathQueryTests
{
    [Fact]
    public void Composes_the_absolute_url_from_the_raw_target()
    {
        var context = PrerenderingTestContext.Create("/products?page=2");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 5001);

        var (absoluteUrl, pathAndQuery) = SpaPrerenderingReflection.GetUnencodedUrlAndPathQuery(context);

        Assert.Equal("https://localhost:5001/products?page=2", absoluteUrl);
        Assert.Equal("/products?page=2", pathAndQuery);
    }

    [Fact]
    public void Passes_percent_escapes_through_unchanged()
    {
        // The whole point of reading RawTarget instead of Request.Path: Node's location.pathname
        // sees the same undecoded string the client sent.
        var context = PrerenderingTestContext.Create("/a=b%20c");
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("example.com");

        var (absoluteUrl, pathAndQuery) = SpaPrerenderingReflection.GetUnencodedUrlAndPathQuery(context);

        Assert.Equal("http://example.com/a=b%20c", absoluteUrl);
        Assert.Equal("/a=b%20c", pathAndQuery);
    }

    [Fact]
    public void Ignores_the_path_base_when_composing_the_url()
    {
        // RawTarget already contains the path base, so it is not prefixed a second time.
        var context = PrerenderingTestContext.Create("/app/home");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Request.PathBase = "/app";

        var (absoluteUrl, _) = SpaPrerenderingReflection.GetUnencodedUrlAndPathQuery(context);

        Assert.Equal("https://example.com/app/home", absoluteUrl);
    }
}

public class ServePrerenderResultTests
{
    [Fact]
    public async Task Redirects_temporarily_when_no_status_code_is_supplied()
    {
        var context = PrerenderingTestContext.Create();

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { RedirectUrl = "/login" });

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/login", context.Response.Headers[HeaderNames.Location].ToString());
    }

    [Fact]
    public async Task Redirects_permanently_only_for_status_code_301()
    {
        var context = PrerenderingTestContext.Create();

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { RedirectUrl = "/moved", StatusCode = 301 });

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("/moved", context.Response.Headers[HeaderNames.Location].ToString());
    }

    [Fact]
    public async Task Treats_308_as_a_permanent_redirect()
    {
        // 308 is the permanent counterpart of 307. Only 301 used to be honoured, so a 308 from the
        // prerenderer was downgraded to a 302, losing both the permanence and the method-preserving
        // semantics the prerenderer asked for.
        var context = PrerenderingTestContext.Create();

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { RedirectUrl = "/moved", StatusCode = 308 });

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
    }

    [Fact]
    public async Task Treats_an_unspecified_redirect_status_code_as_temporary()
    {
        var context = PrerenderingTestContext.Create();

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { RedirectUrl = "/moved" });

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
    }

    [Fact]
    public async Task Reports_unsupported_globals_even_when_the_result_is_a_redirect()
    {
        // The Globals guard used to live in the else-branch only, so an unsupported Globals payload
        // was silently dropped whenever a RedirectUrl was present - while the very same payload on a
        // rendered page threw. Now both paths report it.
        var context = PrerenderingTestContext.Create();
        var result = new RenderToStringResult
        {
            RedirectUrl = "/login",
            Globals = Newtonsoft.Json.Linq.JObject.Parse("""{ "answer": 42 }"""),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SpaPrerenderingReflection.ServePrerenderResult(context, result));
    }

    [Fact]
    public async Task Rejects_globals_on_a_rendered_page()
    {
        var context = PrerenderingTestContext.Create(responseBody: new MemoryStream());
        var result = new RenderToStringResult
        {
            Html = "<html></html>",
            Globals = Newtonsoft.Json.Linq.JObject.Parse("""{ "answer": 42 }"""),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SpaPrerenderingReflection.ServePrerenderResult(context, result));

        Assert.Contains(nameof(RenderToStringResult.Globals), exception.Message);
    }

    [Fact]
    public async Task Writes_the_html_as_a_text_html_response()
    {
        var body = new MemoryStream();
        var context = PrerenderingTestContext.Create(responseBody: body);

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { Html = "<html>hi</html>" });

        Assert.Equal("text/html", context.Response.ContentType);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("<html>hi</html>", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Applies_the_status_code_from_the_render_result()
    {
        var body = new MemoryStream();
        var context = PrerenderingTestContext.Create(responseBody: body);

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { Html = "not found", StatusCode = 404 });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("text/html", context.Response.ContentType);
    }

    [Fact]
    public async Task Keeps_headers_and_status_written_by_the_inner_middleware()
    {
        var body = new MemoryStream();
        var context = PrerenderingTestContext.Create(responseBody: body);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.Headers["X-Inner"] = "leftover";
        context.Response.Headers.ETag = "\"template\"";

        await SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult { Html = "<html></html>" });

        // This test used to assert the exact opposite - a 200 and no X-Inner - because
        // ServePrerenderResult opened with Response.Clear(). That is the bug in issue #81: clearing
        // took every upstream security header with it, and reset a status the application had set
        // deliberately. Only headers describing the captured template are removed now, so an unknown
        // header survives and the ETag does not.
        //
        // Do not "fix" this back.
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("leftover", context.Response.Headers["X-Inner"]);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));
    }

    [Fact]
    public async Task Explains_that_prerendering_returned_no_html()
    {
        // A result with neither RedirectUrl nor Html used to reach WriteAsync(null) and surface as
        // "ArgumentNullException (Parameter 'text')", which says nothing about prerendering.
        var context = PrerenderingTestContext.Create(responseBody: new MemoryStream());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SpaPrerenderingReflection.ServePrerenderResult(context, new RenderToStringResult()));

        Assert.Contains("no HTML", ex.Message);
    }
}

public class UseSpaPrerenderingGuardTests
{
    private sealed class UnusableSpaBuilder(Core.SpaOptions options) : Abstractions.ISpaBuilder
    {
        // The guards under test all run before the builder is dereferenced; touching it means the
        // test escaped into the middleware body, which would try to start Node.
        public IApplicationBuilder ApplicationBuilder => throw new InvalidOperationException("The test reached the middleware body.");

        public Abstractions.ISpaOptions Options { get; } = options;
    }

    [Fact]
    public void Rejects_a_null_spa_builder()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => SpaPrerenderingExtensions.UseSpaPrerendering(null!, options => options.BootModulePath = "dist/main.js"));

        Assert.Equal("spaBuilder", exception.ParamName);
    }

    [Fact]
    public void Rejects_a_null_configuration_callback()
    {
        var builder = new UnusableSpaBuilder(new Core.SpaOptions());

        var exception = Assert.Throws<ArgumentNullException>(() => builder.UseSpaPrerendering(null!));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_an_empty_boot_module_path(string? bootModulePath)
    {
        var builder = new UnusableSpaBuilder(new Core.SpaOptions());

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.UseSpaPrerendering(options => options.BootModulePath = bootModulePath!));

        Assert.Contains(nameof(SpaPrerenderingOptions.BootModulePath), exception.Message);
    }

    [Fact]
    public void Validates_the_builder_before_running_the_configuration_callback()
    {
        var configurationWasInvoked = false;

        Assert.Throws<ArgumentNullException>(
            () => SpaPrerenderingExtensions.UseSpaPrerendering(null!, _ => configurationWasInvoked = true));

        Assert.False(configurationWasInvoked);
    }
}

public class JavaScriptModuleExportTests
{
    [Fact]
    public void Keeps_the_module_name_it_was_constructed_with()
    {
        Assert.Equal("dist/main.js", new JavaScriptModuleExport("dist/main.js").ModuleName);
    }

    [Fact]
    public void Has_no_export_name_until_one_is_assigned()
    {
        var moduleExport = new JavaScriptModuleExport("dist/main.js") { ExportName = "renderModule" };

        Assert.Null(new JavaScriptModuleExport("dist/main.js").ExportName);
        Assert.Equal("renderModule", moduleExport.ExportName);
    }

    [Fact]
    public void Accepts_a_null_module_name()
    {
        // Pins current behaviour: there is no guard here. UseSpaPrerendering is the only caller and
        // validates the path itself, so an invalid module name never reaches Node from that route.
        Assert.Null(new JavaScriptModuleExport(null!).ModuleName);
    }
}

public class AngularPrerendererBuilderBuildTests
{
    private sealed class UnusableSpaBuilder(Core.SpaOptions options) : Abstractions.ISpaBuilder
    {
        public IApplicationBuilder ApplicationBuilder => throw new InvalidOperationException("The test reached the npm script runner.");

        public Abstractions.ISpaOptions Options { get; } = options;
    }

    [Fact]
    public async Task Rejects_a_build_without_a_source_path()
    {
        // The SourcePath guard runs before anything spawns npm, so this exercises Build without
        // ever starting a node process.
        var builder = new AngularPrerendererBuilder("build:ssr");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.Build(new UnusableSpaBuilder(new Core.SpaOptions())));

        Assert.Contains(nameof(Core.SpaOptions.SourcePath), exception.Message);
    }
}
