using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// Builds and invokes the <see cref="SpaPrerenderingExtensions.UseSpaPrerendering"/> middleware
/// delegate in-process, with no node process, and captures the exact <c>originalHtml</c> string the
/// prerenderer would have received.
/// </summary>
internal static class PrerenderingHarness
{
    /// <summary>
    /// Minimal <see cref="Abstractions.ISpaBuilder"/> over a real <see cref="ApplicationBuilder"/>,
    /// unlike UnusableSpaBuilder which exists to prove the guards run before the body.
    /// </summary>
    internal sealed class HarnessSpaBuilder(IApplicationBuilder applicationBuilder, Core.SpaOptions options)
        : Abstractions.ISpaBuilder
    {
        public IApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

        public Abstractions.ISpaOptions Options { get; } = options;
    }

    /// <summary>
    /// Fails the test if the middleware ever tries to talk to node. Registered in the container so
    /// that GetNodeServices takes its "use the registered instance" branch and never constructs a
    /// NodeServicesOptions / node instance factory at all.
    /// </summary>
    internal sealed class ExplodingNodeServices : INodeServices
    {
        public Task<T> InvokeAsync<T>(string moduleName, params object[] args) => throw Boom();

        public Task<T> InvokeAsync<T>(CancellationToken cancellationToken, string moduleName, params object[] args) => throw Boom();

        public Task<T> InvokeExportAsync<T>(string moduleName, string exportedFunctionName, params object[] args) => throw Boom();

        public Task<T> InvokeExportAsync<T>(CancellationToken cancellationToken, string moduleName, string exportedFunctionName, params object[] args) => throw Boom();

        public void Dispose() { }

        private static InvalidOperationException Boom()
            => new("The middleware reached node, but this test expected it to bail out first.");
    }

    /// <summary>
    /// Lets the render proceed, and records the cancellation token it was handed.
    /// </summary>
    /// <remarks>
    /// The token-less overloads throw rather than return, so a callsite that stops passing a token -
    /// which compiles silently, since both overloads end in <c>params object[] args</c> - fails a
    /// test instead of quietly losing cancellation again.
    /// </remarks>
    internal sealed class RecordingNodeServices : INodeServices
    {
        public bool WasInvoked { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public string Html { get; set; } = "<html><body>prerendered</body></html>";

        /// <summary>Runs inside the invocation, while the render token is still alive.</summary>
        public Action? OnInvoke { get; set; }

        /// <summary>Whether the render token was cancelled after <see cref="OnInvoke"/> ran.</summary>
        public bool TokenCancelledDuringInvoke { get; private set; }

        public Task<T> InvokeAsync<T>(string moduleName, params object[] args) => throw TokenlessOverload();

        public Task<T> InvokeAsync<T>(CancellationToken cancellationToken, string moduleName, params object[] args) => throw TokenlessOverload();

        public Task<T> InvokeExportAsync<T>(string moduleName, string exportedFunctionName, params object[] args) => throw TokenlessOverload();

        public Task<T> InvokeExportAsync<T>(CancellationToken cancellationToken, string moduleName, string exportedFunctionName, params object[] args)
        {
            WasInvoked = true;
            ReceivedToken = cancellationToken;

            OnInvoke?.Invoke();
            TokenCancelledDuringInvoke = cancellationToken.IsCancellationRequested;

            cancellationToken.ThrowIfCancellationRequested();

            var result = new MintPlayer.AspNetCore.SpaServices.Prerendering.RenderToStringResult { Html = Html };
            return Task.FromResult((T)(object)result);
        }

        public void Dispose() { }

        private static InvalidOperationException TokenlessOverload()
            => new("The prerenderer invoked node through an overload that discards the cancellation token.");
    }

    internal sealed class HarnessWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Harness";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = Environments.Production;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    }

    internal sealed class HarnessApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    /// <summary>
    /// Records customData["originalHtml"] and, by default, makes the middleware bail out before node
    /// by setting a non-2xx status code, which the middleware re-checks immediately after
    /// OnSupplyData. Set <see cref="StatusCodeToSet"/> to 200 to let the render proceed.
    /// </summary>
    internal sealed class RecordingPrerenderingService : ISpaPrerenderingService
    {
        public bool WasCalled { get; private set; }
        public string? OriginalHtml { get; private set; }
        public IDictionary<string, object>? Data { get; private set; }
        public int StatusCodeToSet { get; set; } = StatusCodes.Status302Found;

        public Task BuildRoutes(ISpaRouteBuilder routeBuilder) => Task.CompletedTask;

        public Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
        {
            WasCalled = true;
            Data = data;
            OriginalHtml = data.TryGetValue("originalHtml", out var value) ? value as string : null;
            httpContext.Response.StatusCode = StatusCodeToSet;
            return Task.CompletedTask;
        }
    }

    internal sealed record Result(
        RecordingPrerenderingService Service,
        DefaultHttpContext Context,
        MemoryStream ClientBody,
        RecordingNodeServices? NodeServices);

    /// <summary>
    /// Builds the real middleware pipeline (prerendering middleware + a fake inner pipeline) and
    /// runs one request through it.
    /// </summary>
    public static async Task<Result> Run(
        RequestDelegate innerPipeline,
        string rawTarget = "/",
        bool registerNodeServices = true,
        Action<DefaultHttpContext>? configureContext = null,
        RecordingNodeServices? recordingNodeServices = null,
        int statusCodeFromOnSupplyData = StatusCodes.Status302Found,
        HarnessApplicationLifetime? lifetime = null)
    {
        var service = new RecordingPrerenderingService { StatusCodeToSet = statusCodeFromOnSupplyData };

        var services = new ServiceCollection();
        if (recordingNodeServices != null)
        {
            services.AddSingleton<INodeServices>(recordingNodeServices);
        }
        else if (registerNodeServices)
        {
            services.AddSingleton<INodeServices>(new ExplodingNodeServices());
        }

        var applicationServices = services
            .AddSingleton<IWebHostEnvironment>(new HarnessWebHostEnvironment())
            .AddSingleton<IHostApplicationLifetime>(lifetime ?? new HarnessApplicationLifetime())
            .BuildServiceProvider();

        var applicationBuilder = new ApplicationBuilder(applicationServices);
        var spaBuilder = new HarnessSpaBuilder(applicationBuilder, new Core.SpaOptions());

        // Registers the middleware under test, and resolves INodeServices / IHostApplicationLifetime /
        // IWebHostEnvironment eagerly right here.
        spaBuilder.UseSpaPrerendering(options => options.BootModulePath = "dist/server/main.js");

        // The inner "next" is ours. Registered after, so next() lands on it instead of on
        // ApplicationBuilder's default 404 terminal.
        applicationBuilder.Run(innerPipeline);

        var pipeline = applicationBuilder.Build();

        var clientBody = new MemoryStream();
        var context = PrerenderingTestContext.Create(rawTarget, clientBody);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 5001);
        context.RequestServices = new ServiceCollection()
            .AddSingleton<ISpaPrerenderingService>(service)
            .BuildServiceProvider();

        configureContext?.Invoke(context);

        await pipeline(context);

        return new Result(service, context, clientBody, recordingNodeServices);
    }

    /// <summary>An inner pipeline that answers 200 text/html with the given body in one write.</summary>
    public static RequestDelegate HtmlPage(string html) => async context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    };

    /// <summary>
    /// An inner pipeline that answers 200 text/html, writing the body in the given chunks.
    /// </summary>
    /// <remarks>
    /// Chunking is what drives <see cref="MemoryStream"/> capacity growth, and therefore the
    /// padding in Defect 1. The real static-file path copies in 16 KiB chunks
    /// (<c>SendFileFallback</c>), so a single write is not representative of production.
    /// </remarks>
    public static RequestDelegate HtmlPageInChunks(params byte[][] chunks) => async context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength = chunks.Sum(c => c.Length);
        foreach (var chunk in chunks)
        {
            await context.Response.Body.WriteAsync(chunk);
        }
    };

    /// <summary>
    /// An inner pipeline that mimics what StaticFileMiddleware leaves behind on an aborted request:
    /// status and headers applied, then the body send swallowed, so nothing is ever written.
    /// </summary>
    public static RequestDelegate AbortedStaticFile(long declaredLength) => context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength = declaredLength;

        // StaticFileContext.SendAsync catches OperationCanceledException and only logs it, so no
        // exception reaches the prerendering middleware.
        return Task.CompletedTask;
    };

    /// <summary>Builds an HTML page of exactly <paramref name="totalBytes"/> UTF-8 bytes.</summary>
    public static string HtmlOfSize(int totalBytes)
    {
        const string prefix = "<html><body><app-root></app-root><!--";
        const string suffix = "--></body></html>";
        var filler = totalBytes - prefix.Length - suffix.Length;
        Assert.True(filler >= 0, "requested size is smaller than the surrounding markup");
        return prefix + new string('x', filler) + suffix;
    }
}

public class OriginalHtmlCaptureTests
{
    private const string SmallIndexHtml = "<html><head><title>t</title></head><body><app-root></app-root></body></html>";

    [Fact]
    public async Task Captures_the_original_html_without_launching_node()
    {
        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(SmallIndexHtml));

        Assert.True(result.Service.WasCalled);
        Assert.Equal(SmallIndexHtml, result.Service.OriginalHtml);

        // Bailing out with a 302 means node was never reached; ExplodingNodeServices would have
        // thrown, and ServePrerenderResult would have overwritten the status code.
        Assert.Equal(StatusCodes.Status302Found, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task Reads_only_the_bytes_written_when_the_body_needs_more_than_one_write()
    {
        // The load-bearing Defect 1 case, and the shape a real ng build index.html has: over 16 KiB,
        // so the static-file copy loop writes it in more than one chunk. 16384 + 3616 bytes grows the
        // MemoryStream to a capacity of 32768, and reading GetBuffer() without a length appended the
        // 12768 unused bytes as NULs.
        var html = PrerenderingHarness.HtmlOfSize(20_000);
        var bytes = Encoding.UTF8.GetBytes(html);

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPageInChunks(
            bytes[..16384],
            bytes[16384..]));

        Assert.Equal(20_000, result.Service.OriginalHtml!.Length);
        Assert.Equal(html, result.Service.OriginalHtml);
        Assert.DoesNotContain('\0', result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Reads_only_the_bytes_written_for_a_body_below_the_minimum_capacity()
    {
        // The other padded branch: a MemoryStream never allocates less than 256 bytes, so anything
        // shorter used to come back padded. 76 bytes written => capacity 256 => 180 trailing NULs.
        Assert.Equal(76, Encoding.UTF8.GetByteCount(SmallIndexHtml));

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(SmallIndexHtml));

        Assert.Equal(76, result.Service.OriginalHtml!.Length);
        Assert.DoesNotContain('\0', result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Reads_a_body_that_lands_exactly_on_a_capacity_boundary()
    {
        // Trap control, and the reason the other two tests above exist. A single write of anything
        // from 256 B to 16 KiB sets Capacity == Length exactly, so this assertion passed even with
        // the defect present - which is how a 547-byte demo template hid it for years.
        var html = PrerenderingHarness.HtmlOfSize(547);

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(html));

        Assert.Equal(html, result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Strips_a_utf8_byte_order_mark()
    {
        // Defect 1b: Encoding.UTF8.GetString does not strip a BOM, so an index.html saved
        // UTF-8-with-BOM put U+FEFF in front of the doctype in the SSR template.
        var html = "<!doctype html><html><body><app-root></app-root></body></html>";
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(html)).ToArray();

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPageInChunks(withBom));

        Assert.Equal(html, result.Service.OriginalHtml);
        Assert.StartsWith("<!doctype", result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Strips_a_byte_order_mark_from_a_multi_write_body()
    {
        // BOM and padding composed. The second chunk is 3619 bytes so that the total is 20003: an
        // offset advanced past the BOM without shortening the count would drop the last byte, and a
        // count shortened without advancing would keep the U+FEFF - each shows up as one character.
        var html = PrerenderingHarness.HtmlOfSize(20_000);
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(html)).ToArray();
        Assert.Equal(20_003, withBom.Length);

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPageInChunks(
            withBom[..16384],
            withBom[16384..]));

        Assert.Equal(20_000, result.Service.OriginalHtml!.Length);
        Assert.Equal(html, result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Keeps_a_zero_width_no_break_space_inside_the_document()
    {
        // Only a leading BOM is ours to remove. An interior U+FEFF is a legitimate ZWNBSP, which is
        // why this is a byte-level check on the first three bytes and not a TrimStart('﻿').
        var html = "<html><body>a﻿b</body></html>";

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(html));

        Assert.Equal(html, result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Does_not_call_the_service_for_a_non_html_response()
    {
        // Sanity check on the seam: the canPrerender gate runs before customData is built.
        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/javascript";
            await context.Response.Body.WriteAsync("console.log(1);"u8.ToArray());
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal("console.log(1);", Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Fact]
    public async Task Works_even_without_a_registered_INodeServices()
    {
        // NodeServicesFactory.CreateNodeServices only wraps a Func<INodeInstance>; the node process
        // is launched in the OutOfProcessNodeInstance constructor, which the factory lambda calls on
        // first InvokeExportAsync. So the "no registered instance" branch does not spawn anything.
        var nodeProcessesBefore = System.Diagnostics.Process.GetProcessesByName("node").Length;

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(SmallIndexHtml),
            registerNodeServices: false);

        Assert.Equal(SmallIndexHtml, result.Service.OriginalHtml);
        Assert.Equal(nodeProcessesBefore, System.Diagnostics.Process.GetProcessesByName("node").Length);
    }
}

public class AbortedRequestTests
{
    private static Action<DefaultHttpContext> Aborted() => context =>
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        context.RequestAborted = cts.Token;
    };

    [Fact]
    public async Task Does_not_prerender_an_aborted_request_with_an_empty_body()
    {
        // Defect 2, exactly as reproduced against the real app: static files applies the status and
        // headers before sending, then swallows the cancellation, so the middleware sees a
        // successful text/html response whose body was never written. Prerendering that empty
        // template is what produced Angular's NG05104.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.AbortedStaticFile(declaredLength: 547),
            configureContext: Aborted());

        Assert.False(result.Service.WasCalled);
        Assert.Empty(result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Does_not_throw_out_of_the_middleware_on_an_aborted_request()
    {
        // Locks in that no cancellation token is passed to the pass-through copy.
        // MemoryStream.CopyToAsync checks its token up front and returns a cancelled task, so
        // handing it RequestAborted would throw here on every aborted request.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.AbortedStaticFile(declaredLength: 547),
            configureContext: Aborted());

        Assert.False(result.Service.WasCalled);
    }

    [Fact]
    public async Task Reconciles_a_declared_content_length_with_the_empty_body_it_passes_through()
    {
        // A request whose abort token is cancelled while the connection is still alive - an
        // application-level request timeout, or a linked token, rather than a real disconnect -
        // otherwise leaves ContentLength at what downstream declared while zero bytes were written,
        // and Kestrel fails the response with "Response Content-Length mismatch: too few bytes
        // written". On a genuine socket abort that check is suppressed, so this is unreachable
        // there; it is reachable for a synthetic token.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.AbortedStaticFile(declaredLength: 547),
            configureContext: Aborted());

        Assert.Equal(0, result.Context.Response.ContentLength);
        Assert.Empty(result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Leaves_an_absent_content_length_absent()
    {
        // Adding a Content-Length to a response that did not declare one would change how it is
        // framed, so a chunked pass-through must stay chunked.
        var result = await PrerenderingHarness.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            return Task.CompletedTask;
        });

        Assert.False(result.Service.WasCalled);
        Assert.Null(result.Context.Response.ContentLength);
    }

    [Fact]
    public async Task Passes_through_a_body_that_was_fully_captured_before_the_abort()
    {
        // An abort can also land after the body was completely copied, in which case the buffer
        // holds the whole page. This is why the abort path copies the buffer out instead of simply
        // returning: a bare return would discard a complete, correct response.
        var html = PrerenderingHarness.HtmlOfSize(547);

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(html),
            configureContext: Aborted());

        Assert.False(result.Service.WasCalled);
        Assert.Equal(html, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Fact]
    public async Task Does_not_prerender_an_empty_template_even_without_an_abort()
    {
        // The guard is causal-cause-agnostic: whatever produced an empty template, there is nothing
        // to prerender, and handing it to node yields NG05104 instead of a diagnosable error.
        var result = await PrerenderingHarness.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            return Task.CompletedTask;
        });

        Assert.False(result.Service.WasCalled);
    }

    [Fact]
    public async Task Still_prerenders_a_partially_captured_template()
    {
        // Deliberate, and the reason the abort check is not redundant with the empty-template guard:
        // an abort mid-copy leaves a truncated body, which is non-empty and so passes the guard.
        // Only the abort check catches that case, so this documents the division of labour rather
        // than asserting the truncation is acceptable.
        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPageInChunks(
            Encoding.UTF8.GetBytes("<html><body><app-r")));

        Assert.True(result.Service.WasCalled);
        Assert.Equal("<html><body><app-r", result.Service.OriginalHtml);
    }

    [Fact]
    public async Task Does_not_prerender_when_the_dev_proxy_leaves_no_content_type()
    {
        // Why development never reproduced Defect 2 in 400 real aborts: SpaProxy copies the status
        // and Content-Type immediately before the body, so a cancellation before the send leaves
        // ContentType null and canPrerender already rejects it. Pinned so that a future change to
        // the proxy's ordering shows up here.
        var result = await PrerenderingHarness.Run(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            configureContext: Aborted());

        Assert.False(result.Service.WasCalled);
        Assert.Null(result.Context.Response.ContentType);
    }

    [Fact]
    public async Task Does_not_prerender_when_the_request_opted_out()
    {
        // SkipPrerendering() exists for code that assigns its status from inside a
        // Response.OnStarting callback, which the middleware cannot see in time - SpaRouteService's
        // redirect being the case in this repo. Without it, / returned a 301 with a fully
        // prerendered body attached.
        var html = PrerenderingHarness.HtmlOfSize(547);

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(html),
            configureContext: context => context.SkipPrerendering(),
            statusCodeFromOnSupplyData: StatusCodes.Status200OK);

        Assert.True(result.Service.WasCalled);
        Assert.Equal(html, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }
}

public class PrerenderCancellationTests
{
    [Fact]
    public async Task Passes_a_cancellation_token_to_the_node_invocation()
    {
        // Before this, the callsite bound InvokeExportAsync's token-less overload, which invokes
        // with CancellationToken.None - so a render in flight could not be cancelled by anything.
        // RecordingNodeServices throws from the token-less overloads, so a regression fails here.
        var node = new PrerenderingHarness.RecordingNodeServices();

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(PrerenderingHarness.HtmlOfSize(547)),
            recordingNodeServices: node,
            statusCodeFromOnSupplyData: StatusCodes.Status200OK);

        Assert.True(node.WasInvoked);
        Assert.True(node.ReceivedToken.CanBeCanceled);
        Assert.Equal("<html><body>prerendered</body></html>", Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Fact]
    public async Task The_token_given_to_node_is_cancelled_when_the_application_stops()
    {
        // The render token links the request's abort token with ApplicationStopping, so a shutdown
        // mid-render is observable. Asserted by stopping the host from inside the invocation, since
        // the linked source is disposed as soon as the render returns. Cancelling RequestAborted
        // instead would prove nothing here - the abort check would return before node is reached.
        var lifetime = new PrerenderingHarness.HarnessApplicationLifetime();
        var node = new PrerenderingHarness.RecordingNodeServices
        {
            OnInvoke = lifetime.StopApplication,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(PrerenderingHarness.HtmlOfSize(547)),
            recordingNodeServices: node,
            statusCodeFromOnSupplyData: StatusCodes.Status200OK,
            lifetime: lifetime));

        Assert.True(node.WasInvoked);
        Assert.True(node.TokenCancelledDuringInvoke);
    }
}
