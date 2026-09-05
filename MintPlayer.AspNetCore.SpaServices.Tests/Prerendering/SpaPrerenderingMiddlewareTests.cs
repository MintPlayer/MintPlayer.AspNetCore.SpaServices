using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
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

        /// <summary>Populates <c>RenderToStringResult.RedirectUrl</c>, exercising the redirect branch.</summary>
        public string? RedirectUrl { get; set; }

        /// <summary>Populates <c>RenderToStringResult.StatusCode</c>, which wins over any status the server assigned.</summary>
        public int? StatusCode { get; set; }

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

            var result = new MintPlayer.AspNetCore.SpaServices.Prerendering.RenderToStringResult
            {
                Html = Html,
                RedirectUrl = RedirectUrl,
                StatusCode = StatusCode,
            };
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

        /// <summary>
        /// Runs after the status code is assigned, for tests that need OnSupplyData to do more than
        /// set a status - add a Location header, say. This is the hook a real consumer uses, and
        /// the whole point of issue #81 is that what it writes here has to survive.
        /// </summary>
        public Action<HttpContext>? Configure { get; set; }

        /// <summary>
        /// Written alongside a 3xx status, because the prerender gate treats a 3xx as a redirect
        /// only when it carries a <c>Location</c> - a bare 3xx can legitimately have a rendered body
        /// (300 Multiple Choices), and 304 is kept body-less by the can-this-carry-a-body rule
        /// rather than by being called a redirect. Set to null to produce a locationless 3xx.
        /// </summary>
        public string? LocationToSet { get; set; } = "/redirected";

        public Task BuildRoutes(ISpaRouteBuilder routeBuilder) => Task.CompletedTask;

        public Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
        {
            WasCalled = true;
            Data = data;
            OriginalHtml = data.TryGetValue("originalHtml", out var value) ? value as string : null;
            httpContext.Response.StatusCode = StatusCodeToSet;

            if (LocationToSet != null && StatusCodeToSet is >= 300 and <= 399)
            {
                httpContext.Response.Headers.Location = LocationToSet;
            }

            Configure?.Invoke(httpContext);
            return Task.CompletedTask;
        }
    }

    /// <summary>One log line, as the middleware rendered it.</summary>
    internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    /// <summary>
    /// Collects every log line the middleware writes, so that a diagnostic-only decision (warn once
    /// about a fragment template, do not warn at all for a HEAD) is assertable.
    /// </summary>
    /// <remarks>
    /// The harness registers no <see cref="ILoggerFactory"/> by default, so
    /// <c>LoggerFinder.GetOrCreateLogger</c> hands the middleware <c>NullLogger.Instance</c> and no
    /// log assertion is possible. This provider is registered in the *application* services before
    /// <c>UseSpaPrerendering</c>, because the logger is resolved once at registration time rather
    /// than per request. Nothing but the framework reference is needed for it - deliberately no
    /// dependency on Microsoft.Extensions.Diagnostics.Testing / FakeLogger.
    /// </remarks>
    internal sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(this);

        public void Dispose() { }

        private sealed class CollectingLogger(CollectingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            // Unconditionally enabled: the LoggerFilterOptions default MinLevel is Information, and
            // the lines under test here are Debug.
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider.Entries)
                {
                    provider.Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
                }
            }
        }
    }

    /// <summary>
    /// An <see cref="Abstractions.ISpaPrerendererBuilder"/> that counts builds instead of running one.
    /// </summary>
    /// <remarks>
    /// Returns a completed task rather than one that never completes: asserting on a count fails the
    /// test, whereas a never-completing task would hang the whole run.
    /// </remarks>
    internal sealed class RecordingBootModuleBuilder : Abstractions.ISpaPrerendererBuilder
    {
        private int _buildCount;

        public int BuildCount => Volatile.Read(ref _buildCount);

        public Task Build(Abstractions.ISpaBuilder spaBuilder)
        {
            Interlocked.Increment(ref _buildCount);
            return Task.CompletedTask;
        }
    }

    internal sealed record Result(
        RecordingPrerenderingService Service,
        DefaultHttpContext Context,
        MemoryStream ClientBody,
        RecordingNodeServices? NodeServices,
        IReadOnlyList<LogEntry> Logs);

    /// <summary>
    /// Builds the real middleware pipeline (prerendering middleware + a fake inner pipeline) and
    /// runs one request through it - or <paramref name="requestCount"/> requests through the *same*
    /// pipeline, which is what a per-pipeline latch (the structural warning) has to be tested
    /// against. The returned <see cref="Result"/> is the last request's; <see cref="Result.Logs"/>
    /// spans all of them, because the collecting provider belongs to the pipeline.
    /// </summary>
    public static async Task<Result> Run(
        RequestDelegate innerPipeline,
        string rawTarget = "/",
        bool registerNodeServices = true,
        Action<DefaultHttpContext>? configureContext = null,
        RecordingNodeServices? recordingNodeServices = null,
        int statusCodeFromOnSupplyData = StatusCodes.Status302Found,
        HarnessApplicationLifetime? lifetime = null,
        Action<SpaPrerenderingOptions>? configureOptions = null,
        bool collectLogs = false,
        int requestCount = 1,
        Action<IServiceCollection>? configureServices = null,
        Action<IApplicationBuilder>? configureUpstream = null,
        Action<HttpContext>? onSupplyData = null,
        string? locationFromOnSupplyData = "/redirected")
    {
        var service = new RecordingPrerenderingService
        {
            StatusCodeToSet = statusCodeFromOnSupplyData,
            Configure = onSupplyData,
            LocationToSet = locationFromOnSupplyData,
        };

        var services = new ServiceCollection();
        if (recordingNodeServices != null)
        {
            services.AddSingleton<INodeServices>(recordingNodeServices);
        }
        else if (registerNodeServices)
        {
            services.AddSingleton<INodeServices>(new ExplodingNodeServices());
        }

        var logProvider = new CollectingLoggerProvider();
        if (collectLogs)
        {
            // MinLevel has to be lowered explicitly: LoggerFilterOptions defaults to Information,
            // which would drop the Debug lines these tests assert on before they reach the provider.
            services.AddSingleton<ILoggerFactory>(new LoggerFactory(
                [logProvider],
                new LoggerFilterOptions { MinLevel = LogLevel.Trace }));
        }

        services
            .AddSingleton<IWebHostEnvironment>(new HarnessWebHostEnvironment())
            .AddSingleton<IHostApplicationLifetime>(lifetime ?? new HarnessApplicationLifetime());

        // Lets a test register what a real upstream middleware needs - AddHsts() for UseHsts(),
        // say. Runs last so it can override the harness defaults above.
        configureServices?.Invoke(services);

        var applicationServices = services.BuildServiceProvider();

        var applicationBuilder = new ApplicationBuilder(applicationServices);
        var spaBuilder = new HarnessSpaBuilder(applicationBuilder, new Core.SpaOptions());

        // Registers middleware *upstream* of prerendering, which is where the headers this work is
        // about get written. Must run before UseSpaPrerendering so the ordering matches a real app.
        configureUpstream?.Invoke(applicationBuilder);

        // Registers the middleware under test, and resolves INodeServices / IHostApplicationLifetime /
        // IWebHostEnvironment / ILoggerFactory eagerly right here.
        spaBuilder.UseSpaPrerendering(options =>
        {
            options.BootModulePath = "dist/server/main.js";
            configureOptions?.Invoke(options);
        });

        // The inner "next" is ours. Registered after, so next() lands on it instead of on
        // ApplicationBuilder's default 404 terminal.
        applicationBuilder.Run(innerPipeline);

        var pipeline = applicationBuilder.Build();

        Result? result = null;
        for (var i = 0; i < requestCount; i++)
        {
            var clientBody = new MemoryStream();
            var context = PrerenderingTestContext.Create(rawTarget, clientBody);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost", 5001);
            context.RequestServices = new ServiceCollection()
                .AddSingleton<ISpaPrerenderingService>(service)
                .BuildServiceProvider();

            configureContext?.Invoke(context);

            await pipeline(context);

            // Stands in for the flush. Kestrel runs these at the first write; nothing after the
            // prerendered body is written touches headers, so firing here is equivalent - and
            // without it every OnStarting callback in the pipeline is silently dropped.
            await ((PrerenderingTestContext.CallbackFiringResponseFeature)
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!)
                .FireOnStartingAsync();

            result = new Result(service, context, clientBody, recordingNodeServices, logProvider.Entries);
        }

        return result!;
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

    /// <summary>
    /// An inner pipeline that mimics what StaticFileMiddleware leaves behind for a HEAD request:
    /// status, Content-Type and the *full* Content-Length of the file, and no body at all.
    /// </summary>
    /// <remarks>
    /// Byte-identical in effect to <see cref="AbortedStaticFile"/>, and deliberately a separate
    /// name: the two model different contracts (a HEAD that must report the length the equivalent
    /// GET would, versus an abort whose declared length is now a lie), and conflating them is how a
    /// fix for one silently changes the other.
    /// </remarks>
    public static RequestDelegate StaticFileHead(long declaredLength) => context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength = declaredLength;

        return Task.CompletedTask;
    };

    /// <summary>
    /// An inner pipeline that answers a satisfiable single-range request the way
    /// StaticFileMiddleware does: 206, the file's own Content-Type, a Content-Range, Accept-Ranges,
    /// and only the requested bytes in the body.
    /// </summary>
    /// <remarks>
    /// <c>ContentLength</c> is the *slice* length, faithfully - which is why the declared-versus-
    /// captured check cannot catch issue #80, and the status/Content-Range framing checks must.
    /// </remarks>
    public static RequestDelegate PartialContent(byte[] slice, long from, long to, long total)
        => async context =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = slice.Length;
            context.Response.Headers[HeaderNames.ContentRange] = $"bytes {from}-{to}/{total}";
            context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
            await context.Response.Body.WriteAsync(slice);
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

    /// <summary>
    /// An HTML page of exactly <paramref name="totalBytes"/> UTF-8 bytes that opens the way a real
    /// <c>ng build</c> index.html does, so that a leading slice of it looks exactly like markup.
    /// </summary>
    public static string DoctypeHtmlOfSize(int totalBytes)
    {
        const string prefix = "<!doctype html><html lang=\"en\"><head><title>demo</title></head><body><app-root></app-root><!--";
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
        // Deliberate, and the reason the abort check is not redundant with the other guards: a
        // truncated body is non-empty, so the empty-template guard passes it.
        //
        // Note the division of labour has narrowed. The declared-versus-captured check now catches a
        // partial capture too - see
        // RangeAndTemplateValidityTests.Does_not_prerender_a_capture_shorter_than_its_declared_content_length -
        // so the abort check is the only remaining cover for a *chunked* partial: no ContentLength
        // was declared (HtmlPageInChunks declares the sum of the chunks it actually writes, so
        // captured == declared here and the length check is silent), and nothing at this layer can
        // tell a short capture from a short page. This test asserts the current division of labour,
        // not that the truncation is acceptable.
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

/// <summary>
/// Issue #80 and its class: the capture has to be a *complete, decodable representation*, not
/// merely a 2xx text/html one. Grown out of the scratch reproduction in the investigation, with the
/// assertions inverted now that the middleware rejects those captures.
/// </summary>
public class RangeAndTemplateValidityTests
{
    private const string IndexHtml =
        "<!doctype html><html><head><title>demo</title></head><body><app-root></app-root></body></html>";

    private static Action<DefaultHttpContext> WithRange(string value)
        => context => context.Request.Headers[HeaderNames.Range] = value;

    // ---------------------------------------------------------------------------------------------
    // Framing: partial content
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_prerender_a_single_byte_partial_response()
    {
        // The reported symptom, inverted: `Range: bytes=0-0` made static files answer 206 with the
        // single "<" of "<!doctype html>", and that one byte was handed to the prerenderer as the
        // SSR template. The framing headers are deliberately left intact on the way out - rejecting
        // the template is not a reason to rewrite a correct 206.
        var html = PrerenderingHarness.HtmlOfSize(547);
        var bytes = Encoding.UTF8.GetBytes(html);

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.PartialContent(bytes[..1], 0, 0, bytes.Length),
            rawTarget: "/person",
            configureContext: WithRange("bytes=0-0"));

        Assert.False(result.Service.WasCalled);
        Assert.Equal(bytes[..1], result.ClientBody.ToArray());
        Assert.Equal(StatusCodes.Status206PartialContent, result.Context.Response.StatusCode);
        Assert.Equal("bytes 0-0/547", result.Context.Response.Headers[HeaderNames.ContentRange].ToString());
        Assert.Equal(1, result.Context.Response.ContentLength);
    }

    [Fact]
    public async Task Does_not_prerender_a_markup_shaped_partial_slice()
    {
        // Load-bearing, and not a duplicate of the single-byte case: a 100-byte leading slice of a
        // real index.html *is* well-formed-looking markup, which is why no amount of "does the
        // template look plausible" checking can stand in for the framing check. The first assertion
        // is the one that records that; delete it and this test reads as redundant.
        var html = PrerenderingHarness.DoctypeHtmlOfSize(20_000);
        var bytes = Encoding.UTF8.GetBytes(html);
        var slice = Encoding.UTF8.GetString(bytes[..100]);

        Assert.StartsWith("<!doctype html><html lang=\"en\">", slice);

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.PartialContent(bytes[..100], 0, 99, bytes.Length),
            rawTarget: "/person",
            configureContext: WithRange("bytes=0-99"));

        Assert.False(result.Service.WasCalled);
        Assert.Equal(bytes[..100], result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Does_not_prerender_a_mid_document_partial_slice()
    {
        // The other end of the same shape: a slice from the middle of the document, with no "<html"
        // in it at all.
        var html = PrerenderingHarness.DoctypeHtmlOfSize(20_000);
        var bytes = Encoding.UTF8.GetBytes(html);
        var slice = Encoding.UTF8.GetString(bytes[100..201]);

        Assert.DoesNotContain("<html", slice, StringComparison.OrdinalIgnoreCase);

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.PartialContent(bytes[100..201], 100, 200, bytes.Length),
            rawTarget: "/person",
            configureContext: WithRange("bytes=100-200"));

        Assert.False(result.Service.WasCalled);
        Assert.Equal(bytes[100..201], result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Does_not_prerender_a_two_hundred_that_still_carries_a_content_range()
    {
        // Pins the Content-Range check independently of the status check: a middleware that rewrites
        // a 206 to a 200 without dropping the framing header would otherwise slip a partial
        // representation past a status-only gate.
        var result = await PrerenderingHarness.Run(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                context.Response.Headers[HeaderNames.ContentRange] = "bytes 0-94/95";
                var bytes = Encoding.UTF8.GetBytes(IndexHtml);
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
            },
            rawTarget: "/person");

        Assert.False(result.Service.WasCalled);
        Assert.Equal(IndexHtml, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Theory]
    [InlineData(StatusCodes.Status201Created)]
    [InlineData(StatusCodes.Status202Accepted)]
    [InlineData(StatusCodes.Status203NonAuthoritative)]
    [InlineData(StatusCodes.Status226IMUsed)]
    public async Task Does_not_prerender_any_other_success_status(int statusCode)
    {
        // The status check is "exactly 200", not "2xx minus the ones we know about", so it fails
        // closed on statuses nobody has considered. 203 in particular is by definition a *modified*
        // representation - the same class of bug as a 206 - and 226 is a delta encoding, which is a
        // diff rather than a document. Pinned so a future widening back to IsSuccessStatusCode
        // cannot happen quietly.
        var result = await PrerenderingHarness.Run(
            async context =>
            {
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/html";
                var bytes = Encoding.UTF8.GetBytes(IndexHtml);
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
            },
            rawTarget: "/person");

        Assert.False(result.Service.WasCalled);
        Assert.Equal(IndexHtml, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    // ---------------------------------------------------------------------------------------------
    // Request-side strip
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Strips_the_range_header_before_the_capture()
    {
        // The cause, fixed at the source: with Range gone, static files never computes a range and
        // answers 200 with the whole file. If-Range has to go too - and did before - because
        // StaticFileContext.ComputeIfRange is the only code that can cancel an already-parsed
        // range, so removing only If-Range guaranteed the range was always honoured.
        var sawRange = true;
        var sawIfRange = true;

        await PrerenderingHarness.Run(
            context =>
            {
                sawRange = context.Request.Headers.ContainsKey(HeaderNames.Range);
                sawIfRange = context.Request.Headers.ContainsKey(HeaderNames.IfRange);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                return Task.CompletedTask;
            },
            rawTarget: "/person",
            configureContext: context =>
            {
                context.Request.Headers[HeaderNames.Range] = "bytes=0-0";
                context.Request.Headers[HeaderNames.IfRange] = "\"etag\"";
            });

        Assert.False(sawRange);
        Assert.False(sawIfRange);
    }

    [Fact]
    public async Task Does_not_restore_the_range_header_after_the_capture()
    {
        // Both halves of the asymmetry in one test, because the asymmetry *is* the decision:
        // Accept-Encoding must come back (upstream compression middleware reads it when the
        // prerendered body is written), Range must not (nothing downstream of the capture reads it,
        // and a prerendered body cannot satisfy a byte range anyway). A test asserting only one
        // half invites the other to be "symmetrised" back in.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: "/person",
            configureContext: context =>
            {
                context.Request.Headers[HeaderNames.Range] = "bytes=0-0";
                context.Request.Headers[HeaderNames.AcceptEncoding] = "gzip, br";
            });

        Assert.False(result.Context.Request.Headers.ContainsKey(HeaderNames.Range));
        Assert.Equal("gzip, br", result.Context.Request.Headers[HeaderNames.AcceptEncoding].ToString());
    }

    [Fact]
    public async Task Prerenders_normally_when_a_range_request_is_answered_with_a_full_two_hundred()
    {
        // Control: the header alone is harmless. Whatever serves the page may ignore ranges (the
        // dev-server proxy does), and then the template is complete and prerendering is correct.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: "/person",
            configureContext: WithRange("bytes=0-0"));

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
    }

    // ---------------------------------------------------------------------------------------------
    // Controls that a status-check change must not break
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_prerender_an_unsatisfiable_range_response()
    {
        // Control. Passed before the fix through IsSuccessStatusCode and passes now through the
        // exactly-200 check. Its point is that a future "let's accept any 2xx again" change cannot
        // quietly start accepting a 416.
        var result = await PrerenderingHarness.Run(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.ContentType = "text/html";
                context.Response.Headers[HeaderNames.ContentRange] = "bytes */547";
                return Task.CompletedTask;
            },
            rawTarget: "/person",
            configureContext: WithRange("bytes=999999-"));

        Assert.False(result.Service.WasCalled);
    }

    [Fact]
    public async Task Prerenders_a_multi_range_request_that_downstream_ignored()
    {
        // Control, observed against the real app: StaticFileMiddleware does not implement
        // multipart/byteranges, so more than one range makes it ignore the header entirely and
        // answer 200 with the whole file.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: "/person",
            configureContext: WithRange("bytes=0-0,2-2"));

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
    }

    [Theory]
    [InlineData("bananas=1-2")]
    [InlineData("bytes=abc")]
    [InlineData("items=0-0")]
    public async Task Prerenders_a_malformed_range_request_that_downstream_ignored(string range)
    {
        // Control. ASP.NET Core ignores all three, so the response is a full 200. The unit row
        // ("items=0-0") is here rather than in a comment because neither RangeHelper.ParseRange nor
        // RangeHeaderValue validates the range *unit* - the unit is checked by StaticFileContext
        // separately, and a check that trusted the header's shape would be wrong about it.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: "/person",
            configureContext: WithRange(range));

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
    }

    // ---------------------------------------------------------------------------------------------
    // Content-Encoding
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_prerender_a_capture_with_a_content_encoding()
    {
        // Accept-Encoding is stripped from the request so that the capture is plain text, but
        // nothing verified the result. A still-encoded capture would be decoded as UTF-8 and hand
        // the prerenderer compressed bytes.
        var compressed = new byte[] { 0x1b, 0x2e, 0x00, 0xf8, 0x25, 0x84, 0xcf, 0xd1, 0xff, 0x9c };

        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.Headers[HeaderNames.ContentEncoding] = "br";
            context.Response.ContentLength = compressed.Length;
            await context.Response.Body.WriteAsync(compressed);
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(compressed, result.ClientBody.ToArray());
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("IDENTITY")]
    public async Task Prerenders_a_capture_that_declares_identity_encoding(string encoding)
    {
        // Guards the encoding check against over-firing: `identity` means no coding was applied, and
        // content codings are case-insensitive.
        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.Headers[HeaderNames.ContentEncoding] = encoding;
            var bytes = Encoding.UTF8.GetBytes(IndexHtml);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
        });

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
    }

    // ---------------------------------------------------------------------------------------------
    // Decode integrity
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_prerender_a_capture_that_is_not_valid_utf8()
    {
        // A compressed capture that arrives with no Content-Encoding header at all - the second net
        // under the encoding check - and equally a genuinely corrupt body. 0xC3 starts a two-byte
        // sequence that never completes, so the bytes are not valid UTF-8 however much real markup
        // surrounds them.
        var body = Encoding.UTF8.GetBytes("<html><body>")
            .Concat(new byte[] { 0xC3 })
            .Concat(Encoding.UTF8.GetBytes("</body></html>"))
            .ToArray();

        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(body, result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Does_not_prerender_a_utf16_encoded_template()
    {
        // UTF-16 markup is byte-wise *valid* UTF-8 - every ASCII character becomes a byte pair whose
        // second byte is 0x00 - so it decodes to "<\0!\0d\0..." and reaches the prerenderer as
        // NG05104. Note char.IsWhiteSpace('\0') is false, which is why the empty-template guard
        // never caught it and an explicit NUL check is needed.
        Assert.False(char.IsWhiteSpace('\0'));

        var body = Encoding.Unicode.GetBytes(IndexHtml);

        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-16";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(body, result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Prerenders_a_template_containing_a_literal_replacement_character()
    {
        // Pins the choice of validating the *bytes* with Utf8.IsValid over scanning the decoded
        // string for U+FFFD. A replacement character is legitimate page content, and rejecting it
        // would turn a working deployment off. Without this test the byte check looks like an
        // over-complicated way to spell Contains('�').
        var html = "<!doctype html><html><body><app-root></app-root><p>�</p></body></html>";

        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(html));

        Assert.True(result.Service.WasCalled);
        Assert.Equal(html, result.Service.OriginalHtml);
    }

    // ---------------------------------------------------------------------------------------------
    // Declared versus captured
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_prerender_a_capture_shorter_than_its_declared_content_length()
    {
        // A short capture against a declared length means the body was truncated or never flushed,
        // whatever the status says - and unlike the abort check this needs no abort to fire, which
        // covers an unflushed PipeWriter write and a body send that failed silently.
        //
        // This deliberately reverses SOLUTION-defect2-abort.md section 2, whose premise was that a
        // transformer might legitimately leave a stale Content-Length. It cannot: Kestrel's own
        // Content-Length verification would already fail every such response on every route this
        // middleware never touches. See Prerenders_a_response_a_transforming_middleware_shrank for
        // the shape that premise was really about.
        var html = PrerenderingHarness.HtmlOfSize(20_000);
        var bytes = Encoding.UTF8.GetBytes(html);

        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = 20_000;
            await context.Response.Body.WriteAsync(bytes[..8192]);
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(bytes[..8192], result.ClientBody.ToArray());
    }

    [Fact]
    public async Task Prerenders_a_response_a_transforming_middleware_shrank()
    {
        // The regression guard for the declared-versus-captured decision, modelled on
        // UseWebMarkupMin: it substitutes the response body, lets the inner pipeline declare and
        // write the full page, then writes the minified page and *updates* ContentLength to match.
        // Captured then equals declared and the length check is silent. If that check ever has to be
        // removed, this is the test that will have failed.
        var full = PrerenderingHarness.HtmlOfSize(547);
        var minified = PrerenderingHarness.HtmlOfSize(456);
        var minifiedBytes = Encoding.UTF8.GetBytes(minified);

        var result = await PrerenderingHarness.Run(async context =>
        {
            var writeFullPage = PrerenderingHarness.HtmlPage(full);

            var clientStream = context.Response.Body;
            using var transformBuffer = new MemoryStream();
            context.Response.Body = transformBuffer;
            try
            {
                await writeFullPage(context);
            }
            finally
            {
                context.Response.Body = clientStream;
            }

            Assert.Equal(547, transformBuffer.Length);
            Assert.Equal(547, context.Response.ContentLength);

            context.Response.ContentLength = minifiedBytes.Length;
            await context.Response.Body.WriteAsync(minifiedBytes);
        });

        Assert.True(result.Service.WasCalled);
        Assert.Equal(minified, result.Service.OriginalHtml);
        Assert.Equal(456, result.Service.OriginalHtml!.Length);
    }

    [Fact]
    public async Task Prerenders_a_short_chunked_capture_with_no_declared_length()
    {
        // The length check makes no claim without a declared length, and must not invent one: on a
        // chunked response nothing at this layer can tell a short capture from a short page.
        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(IndexHtml));
        });

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
        Assert.Null(result.Context.Response.ContentLength);
    }

    [Fact]
    public async Task Prerenders_a_capture_longer_than_its_declared_content_length()
    {
        // Pins the check's one-directionality. A capture *longer* than the declared length cannot be
        // a truncation, so it is not this check's business. "Reject on any mismatch" is the obvious
        // simplification, and this records that it was declined.
        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = 10;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(IndexHtml));
        });

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
    }

    // ---------------------------------------------------------------------------------------------
    // Structural warning
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prerenders_a_fragment_template_without_an_html_element()
    {
        // A fragment template is a legitimate if unusual deployment - the renderer normalizes a bare
        // <app-root></app-root> into a full document - so the structural check logs and never
        // rejects. Rejecting here would break a working application.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage("<app-root></app-root>"),
            collectLogs: true);

        Assert.True(result.Service.WasCalled);
        Assert.Equal("<app-root></app-root>", result.Service.OriginalHtml);

        var warning = Assert.Single(result.Logs, e => e.Level == LogLevel.Warning);
        Assert.Contains("no <html> element", warning.Message);
    }

    [Fact]
    public async Task Does_not_warn_about_a_minified_template_whose_closing_tags_were_removed()
    {
        // The end tags for html and body are optional, so an HTML minifier legitimately strips
        // them - and this check originally required a closing </html> as well, which reported a
        // perfectly good minified document as having no <html> element at all. Observed in the demo,
        // which runs UseWebMarkupMin inside the SPA callback and so downstream of the capture.
        var minified = "<!DOCTYPE html><html lang=en><head><title>t</title></head><body><app-root></app-root>";

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(minified),
            collectLogs: true);

        Assert.True(result.Service.WasCalled);
        Assert.Equal(minified, result.Service.OriginalHtml);
        Assert.DoesNotContain(result.Logs, e => e.Message.Contains("no <html> element", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Warns_once_about_a_fragment_template_and_then_stays_quiet()
    {
        // Two requests through the *same* pipeline. A fragment deployment must not emit a warning
        // per request forever, but a consumer whose template is silently wrong needs to see
        // something at default level at least once - so the first is a Warning and every later one
        // is the same message at Debug.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage("<app-root></app-root>"),
            collectLogs: true,
            requestCount: 2);

        var structural = result.Logs.Where(e => e.Message.Contains("no <html> element")).ToList();

        Assert.Equal(2, structural.Count);
        Assert.Single(structural, e => e.Level == LogLevel.Warning);
        Assert.Single(structural, e => e.Level == LogLevel.Debug);

        // The latch is scoped to the UseSpaPrerendering call, not static, so a second pipeline gets
        // its own Warning. That is the right lifetime: a process hosting two SPAs must hear about
        // both.
        var second = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage("<app-root></app-root>"),
            collectLogs: true);

        Assert.Single(second.Logs, e => e.Level == LogLevel.Warning);
    }
}

/// <summary>
/// Prerendering applies to GET only. Before the gate a HEAD entered the capture, blocked on the SSR
/// bundle build, logged a warning about the (correctly) empty template, and had its
/// <c>Content-Length</c> rewritten to 0 - a 200 text/html claiming zero bytes.
/// </summary>
public class RequestMethodGateTests
{
    private const string IndexHtml =
        "<!doctype html><html><head><title>demo</title></head><body><app-root></app-root></body></html>";

    private static Action<DefaultHttpContext> WithMethod(string method)
        => context => context.Request.Method = method;

    [Fact]
    public async Task A_head_request_keeps_the_full_content_length_it_was_given()
    {
        // The shipped regression, pinned: a HEAD must report the length the equivalent GET would
        // return (RFC 9110 9.3.2), and Kestrel does not catch a wrong one because
        // VerifyResponseContentLength skips HEAD.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.StaticFileHead(declaredLength: 547),
            configureContext: WithMethod(HttpMethods.Head));

        Assert.Equal(547, result.Context.Response.ContentLength);
        Assert.Empty(result.ClientBody.ToArray());
    }

    [Fact]
    public async Task A_head_request_does_not_reach_the_prerenderer()
    {
        // Pins the *gate* rather than the empty-template guard: this inner pipeline does write a
        // body, so nothing downstream of the gate would have turned the request away.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            configureContext: WithMethod(HttpMethods.Head));

        Assert.False(result.Service.WasCalled);
        Assert.Equal(IndexHtml, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
        Assert.Equal(Encoding.UTF8.GetByteCount(IndexHtml), result.Context.Response.ContentLength);
    }

    [Fact]
    public async Task A_head_request_does_not_log_a_warning()
    {
        // A HEAD is entirely healthy traffic - uptime probes, link checkers, CDN revalidation - and
        // used to produce one Warning per request about an empty template that was empty by
        // definition. It is a Debug line now, and it names the method so the reason is legible.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.StaticFileHead(declaredLength: 547),
            configureContext: WithMethod(HttpMethods.Head),
            collectLogs: true);

        Assert.DoesNotContain(result.Logs, e => e.Level == LogLevel.Warning);

        var debug = Assert.Single(result.Logs, e => e.Level == LogLevel.Debug);
        Assert.Contains("HEAD", debug.Message);
    }

    [Fact]
    public async Task A_head_request_does_not_wait_for_the_bootmodule_build()
    {
        // Why the gate sits before the build await: without that ordering a HEAD blocks on
        // `ng build` before being turned away downstream anyway.
        var builder = new PrerenderingHarness.RecordingBootModuleBuilder();

        await PrerenderingHarness.Run(
            PrerenderingHarness.StaticFileHead(declaredLength: 547),
            configureContext: WithMethod(HttpMethods.Head),
            configureOptions: options => options.BootModuleBuilder = builder);

        Assert.Equal(0, builder.BuildCount);
    }

    [Fact]
    public async Task A_post_request_is_passed_through_without_prerendering()
    {
        // A rendered page is meaningless as the response to a POST, and a consumer middleware
        // registered inside the SPA callback that answered one with text/html was previously
        // prerendered. Passed straight through now, body and framing untouched.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            configureContext: WithMethod(HttpMethods.Post));

        Assert.False(result.Service.WasCalled);
        Assert.Equal(IndexHtml, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
        Assert.Equal(Encoding.UTF8.GetByteCount(IndexHtml), result.Context.Response.ContentLength);
    }

    [Fact]
    public async Task An_options_request_does_not_wait_for_the_bootmodule_build()
    {
        // The stated cost of a GET-only gate, made explicit: every other method now skips the build
        // as well, which is the point rather than a side effect.
        var builder = new PrerenderingHarness.RecordingBootModuleBuilder();

        await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            configureContext: WithMethod(HttpMethods.Options),
            configureOptions: options => options.BootModuleBuilder = builder);

        Assert.Equal(0, builder.BuildCount);
    }

    [Fact]
    public async Task A_get_request_is_still_prerendered()
    {
        // Control, guarding against an inverted gate and against the harness's GET default being
        // lost - if it were, every test in this file would silently stop exercising the capture.
        var builder = new PrerenderingHarness.RecordingBootModuleBuilder();

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            configureOptions: options => options.BootModuleBuilder = builder);

        Assert.True(result.Service.WasCalled);
        Assert.Equal(IndexHtml, result.Service.OriginalHtml);
        Assert.Equal(1, builder.BuildCount);
    }

    [Theory]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status205ResetContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task Does_not_rewrite_the_content_length_of_a_bodyless_response(int statusCode)
    {
        // A GET, so the gate cannot help here: these statuses carry no body by definition, so "zero
        // bytes written contradicts the declared length" is simply not true of them, and rewriting
        // the length to 0 discards correct metadata.
        var result = await PrerenderingHarness.Run(context =>
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = 547;
            return Task.CompletedTask;
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(547, result.Context.Response.ContentLength);
        Assert.Empty(result.ClientBody.ToArray());
    }

    [Theory]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status205ResetContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task Reports_a_bodyless_status_at_debug_rather_than_warning(int statusCode)
    {
        // An empty body is *correct* for these statuses, so calling it "a partial representation" at
        // Warning would teach consumers to ignore this category - the same mistake the empty-template
        // guard made by warning on every benign HEAD. Warning is reserved for a response that was
        // supposed to be a document and is not.
        var result = await PrerenderingHarness.Run(
            context =>
            {
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/html";
                context.Response.ContentLength = 547;
                return Task.CompletedTask;
            },
            rawTarget: "/person",
            collectLogs: true);

        Assert.DoesNotContain(result.Logs, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(result.Logs, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains("carries no response body", StringComparison.Ordinal));
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
