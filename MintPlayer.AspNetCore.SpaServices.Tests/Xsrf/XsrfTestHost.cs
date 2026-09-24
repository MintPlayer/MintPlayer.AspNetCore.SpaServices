using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Xsrf;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

/// <summary>
/// Drives the Xsrf middleware over a <see cref="DefaultHttpContext"/> whose response feature both
/// records <c>OnStarting</c> callbacks and fires them in Kestrel's order.
/// </summary>
internal static class XsrfTestHost
{
    public const string RequestToken = "the-request-token";
    public const string CookieToken = "the-cookie-token";

    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public sealed record Result(DefaultHttpContext Context, IReadOnlyList<string> SetCookies, IReadOnlyList<Entry> Logs)
    {
        /// <summary>The single <c>XSRF-TOKEN</c> cookie. Fails the test if there is not exactly one.</summary>
        public string XsrfCookie => SetCookies.Single(c => c.StartsWith("XSRF-TOKEN="));
    }

    public static async Task<Result> Run(
        RequestDelegate? next = null,
        bool https = false,
        Action<XsrfOptions>? configure = null,
        Action<HttpContext>? configureContext = null,
        IAntiforgery? antiforgery = null,
        string? environment = null,
        bool fireCallbacks = true,
        int requests = 1,
        int? status = null)
    {
        var options = new XsrfOptions();
        configure?.Invoke(options);

        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder).SetMinimumLevel(LogLevel.Trace));

        // One instance across every request, the way UseMiddleware builds it - otherwise a
        // "log this once per process" assertion cannot fail.
        var middleware = new Antiforgery(
            next ?? (_ => Task.CompletedTask),
            options,
            new StubHostEnvironment(environment ?? Environments.Development),
            loggerFactory);

        DefaultHttpContext context = null!;
        for (var i = 0; i < requests; i++)
        {
            context = CreateContext(BuildServices(), https);
            if (status is not null)
            {
                context.Response.StatusCode = status.Value;
            }

            configureContext?.Invoke(context);

            await middleware.Invoke(context, antiforgery ?? StubAntiforgery.Working());

            if (fireCallbacks)
            {
                await FireCallbacks(context);
            }
        }

        return new Result(context, ReadSetCookies(context), recorder.Entries);
    }

    public static ServiceProvider BuildServices()
        => new ServiceCollection()
            .AddLogging()
            .AddAntiforgery()
            .AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development))
            .BuildServiceProvider();

    public static DefaultHttpContext CreateContext(IServiceProvider services, bool https = false)
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Scheme = https ? "https" : "http", Path = "/" });
        features.Set<IHttpResponseFeature>(new CallbackFiringResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));

        return new DefaultHttpContext(features) { RequestServices = services };
    }

    public static Task FireCallbacks(HttpContext context)
        => ((CallbackFiringResponseFeature)context.Features.Get<IHttpResponseFeature>()!).FireOnStartingAsync();

    private static IReadOnlyList<string> ReadSetCookies(HttpContext context)
        => [.. context.Response.Headers.SetCookie.Where(c => c is not null).Select(c => c!)];

    /// <summary>
    /// <see cref="HttpResponseFeature.OnStarting"/> is a no-op stub, so callbacks registered against
    /// a bare feature collection are silently dropped and every assertion about the cookie passes
    /// vacuously with nothing ever written.
    /// </summary>
    /// <remarks>
    /// Fires newest-first. Kestrel pushes onto a <see cref="Stack{T}"/> and pops in
    /// <c>FireOnStarting</c>, so a middleware registered early runs <em>last</em> - which is the
    /// whole mechanism behind the cache-header clobbering in issue #85. The suite used to fire these
    /// in registration order, which reversed that relationship and could not have observed it.
    /// </remarks>
    internal sealed class CallbackFiringResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> onStarting = new();
        private bool fired;

        public override bool HasStarted => hasStarted;
        private bool hasStarted;

        public void MarkStarted() => hasStarted = true;

        public override void OnStarting(Func<object, Task> callback, object state)
            => onStarting.Push((callback, state));

        /// <summary>Runs the registered callbacks, once, newest first.</summary>
        public async Task FireOnStartingAsync()
        {
            if (fired) { return; }
            fired = true;

            while (onStarting.Count > 0)
            {
                var (callback, state) = onStarting.Pop();
                await callback(state);
            }
        }
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<Entry> entries = [];

        public IReadOnlyList<Entry> Entries => entries;

        public ILogger CreateLogger(string categoryName) => new Recorder(entries);

        public void Dispose() { }

        private sealed class Recorder(List<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add(new Entry(logLevel, formatter(state, exception), exception));
                }
            }
        }
    }
}

/// <summary>
/// Stands in for <c>DefaultAntiforgery</c>, reproducing the response side effects that matter:
/// the HttpOnly cookie half, <c>X-Frame-Options</c>, and the <c>Cache-Control</c>/<c>Pragma</c>
/// stamping that only happens while the response has not started.
/// </summary>
internal sealed class StubAntiforgery : IAntiforgery
{
    private readonly Func<Exception>? throws;
    private readonly string? requestToken;

    private StubAntiforgery(Func<Exception>? throws, string? requestToken)
    {
        this.throws = throws;
        this.requestToken = requestToken;
    }

    public static StubAntiforgery Working() => new(null, XsrfTestHost.RequestToken);

    public static StubAntiforgery ReturningNullRequestToken() => new(null, null);

    public static StubAntiforgery Throwing(Func<Exception> factory) => new(factory, null);

    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext)
    {
        if (throws is not null)
        {
            throw throws();
        }

        // DefaultAntiforgery.SaveCookieTokenAndHeader - neither of these is guarded by HasStarted.
        httpContext.Response.Cookies.Append(".AspNetCore.Antiforgery.Test", XsrfTestHost.CookieToken,
            new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
        httpContext.Response.Headers.XFrameOptions = "SAMEORIGIN";

        // DefaultAntiforgery.SetDoNotCacheHeaders, which is.
        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.Headers.CacheControl = "no-cache, no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
        }

        return GetTokens(httpContext);
    }

    public AntiforgeryTokenSet GetTokens(HttpContext httpContext)
        => new(requestToken, XsrfTestHost.CookieToken, "__RequestVerificationToken", "X-XSRF-TOKEN");

    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

    public void SetCookieTokenAndHeader(HttpContext httpContext) { }

    public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
}
