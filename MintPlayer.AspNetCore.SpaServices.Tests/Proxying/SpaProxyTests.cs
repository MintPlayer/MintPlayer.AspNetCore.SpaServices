using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Abstractions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Extensions.Proxy;
using MintPlayer.AspNetCore.SpaServices.Proxying;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Proxying;

public class CreateHttpClientForProxyTests
{
    [Fact]
    public void Applies_the_requested_timeout()
    {
        using var client = SpaProxy.CreateHttpClientForProxy(TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), client.Timeout);
    }

    [Fact]
    public void Accepts_an_infinite_timeout()
    {
        // The all-requests proxy uses Timeout.InfiniteTimeSpan so that server-sent-event style
        // responses, which never complete, are not torn down mid-stream.
        using var client = SpaProxy.CreateHttpClientForProxy(Timeout.InfiniteTimeSpan);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void Does_not_follow_redirects()
    {
        // A redirect from the dev server has to reach the browser verbatim; following it here would
        // silently swallow the 3xx and serve the wrong URL's content.
        using var client = SpaProxy.CreateHttpClientForProxy(TimeSpan.FromSeconds(1));

        Assert.False(GetHandler(client).AllowAutoRedirect);
    }

    [Fact]
    public void Does_not_manage_cookies_itself()
    {
        // Cookies belong to the browser session, not to the shared proxy client; letting the handler
        // keep a CookieContainer would leak one visitor's cookies into another visitor's request.
        using var client = SpaProxy.CreateHttpClientForProxy(TimeSpan.FromSeconds(1));

        Assert.False(GetHandler(client).UseCookies);
    }

    /// <summary>
    /// The handler is not exposed by <see cref="HttpClient"/>, so it is pulled off the private field
    /// that <see cref="HttpMessageInvoker"/> stores it in.
    /// </summary>
    private static HttpClientHandler GetHandler(HttpClient client)
    {
        for (var type = client.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.GetValue(client) is HttpClientHandler handler)
                    return handler;
            }
        }

        throw new InvalidOperationException("Could not locate the HttpClientHandler behind the HttpClient.");
    }
}

public class PerformProxyRequestTests
{
    [Fact]
    public async Task Composes_the_target_uri_from_the_base_uri_path_and_query()
    {
        var handler = new StubHandler();
        var context = CreateContext(path: "/api/values", queryString: "?a=1&b=2");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("http://localhost:4200/api/values?a=1&b=2", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Trims_the_single_trailing_slash_off_the_base_uri()
    {
        // Uri.ToString() always renders an authority-only URI with a trailing slash, so without the
        // trim every proxied path would come out double-slashed.
        var handler = new StubHandler();
        var context = CreateContext(path: "/index.html");

        await Proxy(context, handler, "http://localhost:4200");

        Assert.Equal("http://localhost:4200/index.html", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Keeps_a_base_path_when_the_base_uri_has_one()
    {
        var handler = new StubHandler();
        var context = CreateContext(path: "/main.js");

        await Proxy(context, handler, "http://localhost:4200/dist/");

        Assert.Equal("http://localhost:4200/dist/main.js", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Rewrites_the_Host_header_to_the_proxy_target()
    {
        var handler = new StubHandler();
        var context = CreateContext();
        context.Request.Headers["Host"] = "www.example.com";

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("localhost:4200", handler.LastRequest!.Headers.Host);
    }

    [Fact]
    public async Task Forwards_arbitrary_request_headers()
    {
        var handler = new StubHandler();
        var context = CreateContext();
        context.Request.Headers["X-Custom"] = "hello";

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("hello", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Custom")));
    }

    [Fact]
    public async Task Does_not_forward_the_Connection_header()
    {
        // A hop-by-hop header describes the client's connection to us, not ours to the dev server.
        var handler = new StubHandler();
        var context = CreateContext();
        context.Request.Headers["Connection"] = "keep-alive";

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.False(handler.LastRequest!.Headers.Contains("Connection"));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("DELETE")]
    [InlineData("TRACE")]
    public async Task Sends_no_body_for_bodiless_methods(string method)
    {
        var handler = new StubHandler();
        var context = CreateContext(method: method, body: "should-be-ignored");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Null(handler.LastRequest!.Content);
    }

    [Fact]
    public async Task Forwards_the_request_body_for_a_POST()
    {
        var handler = new StubHandler();
        var context = CreateContext(method: "POST", body: "the-payload");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("the-payload", handler.LastRequestBody);
    }

    [Fact]
    public async Task Forwards_content_headers_on_a_bodiless_request()
    {
        // A content header such as Content-Type is rejected by the request header collection and
        // belongs on the content instead. The fallback used to reach for Content only when there
        // was a body, so on a GET the header was dropped with no trace. An empty content now
        // carries it through.
        var handler = new StubHandler();
        var context = CreateContext(method: "GET");
        context.Request.Headers["Content-Type"] = "application/json";

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("application/json", handler.LastRequest!.Content!.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Uses_the_incoming_request_method()
    {
        var handler = new StubHandler();
        var context = CreateContext(method: "PATCH", body: "x");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task Copies_the_status_code_and_body_of_the_proxied_response()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("proxied-body")
        };
        var handler = new StubHandler(response);
        var context = CreateContext();

        var didProxy = await Proxy(context, handler, "http://localhost:4200/");

        Assert.True(didProxy);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal("proxied-body", ReadResponseBody(context));
    }

    [Fact]
    public async Task Copies_response_headers()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        response.Headers.TryAddWithoutValidation("X-Powered-By", "webpack");
        var handler = new StubHandler(response);
        var context = CreateContext();

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("webpack", context.Response.Headers["X-Powered-By"]);
    }

    [Fact]
    public async Task Strips_the_transfer_encoding_response_header()
    {
        // HttpClient has already de-chunked the body, so leaving the header would tell the client to
        // expect a framing that is no longer there.
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        response.Headers.TryAddWithoutValidation("Transfer-Encoding", "chunked");
        var handler = new StubHandler(response);
        var context = CreateContext();

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.False(context.Response.Headers.ContainsKey("Transfer-Encoding"));
    }

    [Fact]
    public async Task Keeps_HTTP1_only_response_headers_when_the_client_speaks_HTTP1()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        var handler = new StubHandler(response);
        var context = CreateContext(protocol: "HTTP/1.1");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.Equal("timeout=5", context.Response.Headers["Keep-Alive"]);
    }

    [Fact]
    public async Task Drops_HTTP1_only_response_headers_when_the_client_speaks_HTTP2()
    {
        // HTTP/2 forbids these connection-specific headers; forwarding them would make the client
        // reject the response as a protocol error.
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        var handler = new StubHandler(response);
        var context = CreateContext(protocol: "HTTP/2");

        await Proxy(context, handler, "http://localhost:4200/");

        Assert.False(context.Response.Headers.ContainsKey("Keep-Alive"));
    }

    [Fact]
    public async Task Returns_false_for_a_404_when_404s_are_not_proxied()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope") });
        var context = CreateContext();

        var didProxy = await Proxy(context, handler, "http://localhost:4200/", proxy404s: false);

        Assert.False(didProxy);
    }

    [Fact]
    public async Task Leaves_the_response_untouched_for_an_unproxied_404()
    {
        // Returning false means "let the rest of the pipeline handle it", so nothing may have been
        // written yet - otherwise the next middleware would be appending to a 404 body.
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope") });
        var context = CreateContext();

        await Proxy(context, handler, "http://localhost:4200/", proxy404s: false);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(context));
    }

    [Fact]
    public async Task Proxies_a_404_when_404s_are_proxied()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("dev-server-404") });
        var context = CreateContext();

        var didProxy = await Proxy(context, handler, "http://localhost:4200/", proxy404s: true);

        Assert.True(didProxy);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("dev-server-404", ReadResponseBody(context));
    }

    [Fact]
    public async Task Proxies_a_non_404_error_status_even_when_404s_are_not_proxied()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        var context = CreateContext();

        var didProxy = await Proxy(context, handler, "http://localhost:4200/", proxy404s: false);

        Assert.True(didProxy);
        Assert.Equal(500, context.Response.StatusCode);
    }

    [Fact]
    public async Task Reports_the_request_as_handled_when_it_is_cancelled()
    {
        // A client that navigated away, or a host that is shutting down, is not an error - and the
        // caller must not fall through to the rest of the pipeline on a half-written response.
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var context = CreateContext();

        Assert.True(await Proxy(context, handler, "http://localhost:4200/"));
    }

    [Fact]
    public async Task Reports_the_request_as_handled_on_a_task_cancellation()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException());
        var context = CreateContext();

        Assert.True(await Proxy(context, handler, "http://localhost:4200/"));
    }

    [Fact]
    public async Task Reports_the_request_as_handled_on_an_IO_error()
    {
        // A torn-down socket during shutdown surfaces as IOException rather than a cancellation.
        var handler = new StubHandler(_ => throw new IOException("socket went away"));
        var context = CreateContext();

        Assert.True(await Proxy(context, handler, "http://localhost:4200/"));
    }

    [Fact]
    public async Task Aborts_before_sending_when_the_client_has_already_disconnected()
    {
        var handler = new StubHandler(token =>
        {
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        });
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var context = CreateContext(requestAborted: aborted.Token);

        Assert.True(await Proxy(context, handler, "http://localhost:4200/"));
    }

    [Fact]
    public async Task Aborts_before_sending_when_the_application_is_stopping()
    {
        var handler = new StubHandler(token =>
        {
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        });
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var context = CreateContext();

        Assert.True(await Proxy(context, handler, "http://localhost:4200/", applicationStopping: stopping.Token));
    }

    [Fact]
    public async Task Rewraps_a_connection_failure_with_a_diagnosable_message()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var context = CreateContext(path: "/main.js");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Proxy(context, handler, "http://localhost:4200/"));

        Assert.Contains("http://localhost:4200/main.js", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task Keeps_the_original_connection_failure_as_the_inner_exception()
    {
        var original = new HttpRequestException("Connection refused");
        var handler = new StubHandler(_ => throw original);
        var context = CreateContext();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Proxy(context, handler, "http://localhost:4200/"));

        Assert.Same(original, ex.InnerException);
    }

    [Fact]
    public async Task Waits_for_the_base_uri_before_sending_anything()
    {
        // The target port is not known until the dev server has finished starting up, so requests
        // that arrive first are parked on the pending base-uri task rather than failing.
        var handler = new StubHandler();
        var context = CreateContext();
        var baseUriSource = new TaskCompletionSource<Uri>();

        var proxying = SpaProxy.PerformProxyRequest(
            context, new HttpClient(handler), baseUriSource.Task, CancellationToken.None, proxy404s: true);

        Assert.False(proxying.IsCompleted);
        Assert.Null(handler.LastRequest);

        baseUriSource.SetResult(new Uri("http://localhost:4200/"));
        Assert.True(await proxying);
        Assert.NotNull(handler.LastRequest);
    }

    private static Task<bool> Proxy(
        DefaultHttpContext context,
        HttpMessageHandler handler,
        string baseUri,
        bool proxy404s = true,
        CancellationToken applicationStopping = default)
        => SpaProxy.PerformProxyRequest(
            context,
            new HttpClient(handler),
            Task.FromResult(new Uri(baseUri)),
            applicationStopping,
            proxy404s);

    internal static DefaultHttpContext CreateContext(
        string path = "/",
        string queryString = "",
        string method = "GET",
        string protocol = "HTTP/1.1",
        string? body = null,
        CancellationToken requestAborted = default)
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = method,
            Path = path,
            QueryString = queryString,
            Protocol = protocol,
            Scheme = "http",
            Body = body is null ? Stream.Null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
        });
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        features.Set<IHttpRequestLifetimeFeature>(new StubLifetimeFeature(requestAborted));

        return new DefaultHttpContext(features);
    }

    internal static string ReadResponseBody(HttpContext context)
    {
        var stream = (MemoryStream)context.Features.Get<IHttpResponseBodyFeature>()!.Stream;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class StubLifetimeFeature(CancellationToken requestAborted) : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = requestAborted;

        public void Abort() { }
    }

    /// <summary>
    /// Stands in for the network. Every proxy test drives the real code path through this handler,
    /// so no test ever opens a socket.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, HttpResponseMessage> respond;

        public StubHandler()
            : this(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") })
        {
        }

        public StubHandler(HttpResponseMessage response) : this(_ => response)
        {
        }

        public StubHandler(Func<CancellationToken, HttpResponseMessage> respond)
        {
            this.respond = respond;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return respond(cancellationToken);
        }
    }
}

public class ConditionalProxyMiddlewareTests
{
    [Fact]
    public async Task Passes_the_request_through_when_an_endpoint_was_already_matched()
    {
        // Routing has already claimed the request, so an API controller must win over the dev server
        // even though the SPA prefix would otherwise match.
        var context = PerformProxyRequestTests.CreateContext(path: "/api/values");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, null, "matched"));
        var nextWasCalled = false;

        await CreateMiddleware("/", _ => { nextWasCalled = true; return Task.CompletedTask; }).Invoke(context);

        Assert.True(nextWasCalled);
    }

    [Fact]
    public async Task Passes_the_request_through_when_the_path_prefix_does_not_match()
    {
        var context = PerformProxyRequestTests.CreateContext(path: "/api/values");
        var nextWasCalled = false;

        await CreateMiddleware("/dist", _ => { nextWasCalled = true; return Task.CompletedTask; }).Invoke(context);

        Assert.True(nextWasCalled);
    }

    [Fact]
    public async Task Matches_only_whole_segments()
    {
        // "/dist" must not swallow "/distribution".
        var context = PerformProxyRequestTests.CreateContext(path: "/distribution/x");
        var nextWasCalled = false;

        await CreateMiddleware("/dist", _ => { nextWasCalled = true; return Task.CompletedTask; }).Invoke(context);

        Assert.True(nextWasCalled);
    }

    [Fact]
    public void Proxies_a_matching_path_instead_of_calling_next()
    {
        var context = PerformProxyRequestTests.CreateContext(path: "/dist/main.js");
        var nextWasCalled = false;

        // The base URI never resolves, which parks the proxy branch exactly where a real request
        // waits for the dev server to come up - and keeps this test off the network.
        var invocation = CreateMiddleware("/dist", _ => { nextWasCalled = true; return Task.CompletedTask; }).Invoke(context);

        Assert.False(invocation.IsCompleted);
        Assert.False(nextWasCalled);
    }

    [Theory]
    [InlineData("dist")]
    [InlineData("/dist")]
    public void Normalises_a_path_prefix_that_is_missing_its_leading_slash(string prefix)
    {
        var context = PerformProxyRequestTests.CreateContext(path: "/dist/main.js");

        var invocation = CreateMiddleware(prefix, _ => Task.CompletedTask).Invoke(context);

        Assert.False(invocation.IsCompleted);
    }

    [Fact]
    public void Treats_a_root_prefix_as_matching_every_path()
    {
        var context = PerformProxyRequestTests.CreateContext(path: "/anything/at/all");
        var nextWasCalled = false;

        var invocation = CreateMiddleware("/", _ => { nextWasCalled = true; return Task.CompletedTask; }).Invoke(context);

        Assert.False(invocation.IsCompleted);
        Assert.False(nextWasCalled);
    }

    [Fact]
    public void Treats_an_empty_prefix_as_root()
    {
        // "" normalises to "/", which the is-root flag then picks up.
        var context = PerformProxyRequestTests.CreateContext(path: "/anything");

        var invocation = CreateMiddleware(string.Empty, _ => Task.CompletedTask).Invoke(context);

        Assert.False(invocation.IsCompleted);
    }

    private static ConditionalProxyMiddleware CreateMiddleware(string pathPrefix, RequestDelegate next)
        => new(
            next,
            pathPrefix,
            TimeSpan.FromSeconds(1),
            new TaskCompletionSource<Uri>().Task,
            new StubApplicationLifetime());
}

public class SpaProxyingExtensionsTests
{
    [Fact]
    public void Registers_a_terminal_proxy_middleware()
    {
        var spaBuilder = CreateSpaBuilder();

        spaBuilder.UseProxyToSpaDevelopmentServer(new Uri("http://localhost:4200/"));

        Assert.NotNull(((IApplicationBuilder)spaBuilder.ApplicationBuilder).Build());
    }

    [Fact]
    public void Accepts_a_base_uri_supplied_as_a_string()
    {
        var spaBuilder = CreateSpaBuilder();

        spaBuilder.UseProxyToSpaDevelopmentServer("http://localhost:4200/");

        Assert.NotNull(((IApplicationBuilder)spaBuilder.ApplicationBuilder).Build());
    }

    [Fact]
    public void Accepts_a_base_uri_factory()
    {
        var spaBuilder = CreateSpaBuilder();

        spaBuilder.UseProxyToSpaDevelopmentServer(() => Task.FromResult(new Uri("http://localhost:4200/")));

        Assert.NotNull(((IApplicationBuilder)spaBuilder.ApplicationBuilder).Build());
    }

    [Fact]
    public void Rejects_a_null_base_uri_string()
    {
        // The Uri constructor is what guards here - the overload has no explicit argument check - so
        // the failure arrives before the builder is ever touched.
        var spaBuilder = new ExplodingSpaBuilder();

        Assert.Throws<ArgumentNullException>(() => spaBuilder.UseProxyToSpaDevelopmentServer((string)null!));
    }

    [Fact]
    public void Rejects_a_base_uri_string_that_is_not_a_uri()
    {
        var spaBuilder = new ExplodingSpaBuilder();

        Assert.Throws<UriFormatException>(() => spaBuilder.UseProxyToSpaDevelopmentServer("not a uri"));
    }

    [Fact]
    public void Rejects_a_relative_base_uri_string()
    {
        // Proxying needs an absolute target; a relative string has no host to forward to.
        var spaBuilder = new ExplodingSpaBuilder();

        Assert.Throws<UriFormatException>(() => spaBuilder.UseProxyToSpaDevelopmentServer("relative/path"));
    }

    [Fact]
    public void Requires_an_application_lifetime_in_the_service_provider()
    {
        // Current behaviour: without IHostApplicationLifetime registered the failure surfaces as a
        // bare InvalidOperationException from DI rather than anything mentioning SPA proxying.
        var spaBuilder = new TestSpaBuilder(new ApplicationBuilder(new ServiceCollection().BuildServiceProvider()));

        Assert.Throws<InvalidOperationException>(
            () => spaBuilder.UseProxyToSpaDevelopmentServer(new Uri("http://localhost:4200/")));
    }

    private static TestSpaBuilder CreateSpaBuilder()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IHostApplicationLifetime>(new StubApplicationLifetime())
            .BuildServiceProvider();

        return new TestSpaBuilder(new ApplicationBuilder(services));
    }

    private sealed class TestSpaBuilder(IApplicationBuilder applicationBuilder) : ISpaBuilder
    {
        public IApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

        public ISpaOptions Options => throw new NotSupportedException();
    }

    /// <summary>Proves an overload validates its arguments before it reaches the pipeline.</summary>
    private sealed class ExplodingSpaBuilder : ISpaBuilder
    {
        public IApplicationBuilder ApplicationBuilder => throw new InvalidOperationException("Should not be reached.");

        public ISpaOptions Options => throw new NotSupportedException();
    }
}

internal sealed class StubApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}
