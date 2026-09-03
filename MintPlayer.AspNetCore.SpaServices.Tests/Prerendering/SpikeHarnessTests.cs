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
/// Spike 1 harness: builds and invokes the <see cref="SpaPrerenderingExtensions.UseSpaPrerendering"/>
/// middleware delegate in-process, with no node process, and captures the exact
/// <c>originalHtml</c> string the prerenderer would have received.
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
            => new("The middleware reached node. The ISpaPrerenderingService bail-out did not work.");
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
    /// Records customData["originalHtml"] and then makes the middleware bail out before node by
    /// setting a non-2xx status code, which the middleware re-checks immediately after OnSupplyData.
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
        MemoryStream ClientBody);

    /// <summary>
    /// Builds the real middleware pipeline (prerendering middleware + a fake inner pipeline) and
    /// runs one request through it.
    /// </summary>
    public static async Task<Result> Run(RequestDelegate innerPipeline, string rawTarget = "/", bool registerNodeServices = true)
    {
        var service = new RecordingPrerenderingService();

        var services = new ServiceCollection();
        if (registerNodeServices)
        {
            services.AddSingleton<INodeServices>(new ExplodingNodeServices());
        }

        var applicationServices = services
            .AddSingleton<IWebHostEnvironment>(new HarnessWebHostEnvironment())
            .AddSingleton<IHostApplicationLifetime>(new HarnessApplicationLifetime())
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

        await pipeline(context);

        return new Result(service, context, clientBody);
    }

    /// <summary>An inner pipeline that answers 200 text/html with the given body.</summary>
    public static RequestDelegate HtmlPage(string html) => async context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html";
        var bytes = Encoding.UTF8.GetBytes(html);
        await context.Response.Body.WriteAsync(bytes);
    };
}

public class SpikeHarnessTests
{
    private const string IndexHtml = "<html><head><title>t</title></head><body><app-root></app-root></body></html>";

    [Fact]
    public async Task Captures_the_original_html_without_launching_node()
    {
        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(IndexHtml));

        Assert.True(result.Service.WasCalled);
        Assert.NotNull(result.Service.OriginalHtml);

        // The captured template starts with exactly what the inner pipeline wrote.
        Assert.StartsWith(IndexHtml, result.Service.OriginalHtml);

        // Bailing out with a 302 means node was never reached; ExplodingNodeServices would have
        // thrown, and ServePrerenderResult would have overwritten the status code.
        Assert.Equal(StatusCodes.Status302Found, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task Shows_the_GetBuffer_padding_defect_verbatim()
    {
        // Defect 1, observed rather than fixed: GetBuffer() is decoded without a length, so the
        // string handed to OnSupplyData is Capacity bytes long, not Length. 76 bytes written =>
        // MemoryStream capacity 256 => 180 trailing NULs.
        var result = await PrerenderingHarness.Run(PrerenderingHarness.HtmlPage(IndexHtml));

        var captured = result.Service.OriginalHtml!;
        Assert.Equal(76, Encoding.UTF8.GetByteCount(IndexHtml));
        Assert.Equal(256, captured.Length);
        Assert.Equal(new string('\0', 180), captured[IndexHtml.Length..]);
    }

    [Fact]
    public async Task Works_even_without_a_registered_INodeServices()
    {
        // Empirical answer to the spike question: NodeServicesFactory.CreateNodeServices only wraps
        // a Func<INodeInstance>; the node process is launched in the OutOfProcessNodeInstance
        // constructor, which the factory lambda calls on first InvokeExportAsync. So the "no
        // registered instance" branch of GetNodeServices does not spawn anything either.
        var nodeProcessesBefore = System.Diagnostics.Process.GetProcessesByName("node").Length;

        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            registerNodeServices: false);

        Assert.StartsWith(IndexHtml, result.Service.OriginalHtml);
        Assert.Equal(nodeProcessesBefore, System.Diagnostics.Process.GetProcessesByName("node").Length);
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
}
