using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Routing;

/// <summary>
/// Builds a real <see cref="ISpaRouteService"/> over a caller-supplied route definition.
/// <para>
/// The service is resolved from a real container via the generated <c>AddSpaRouteServices()</c>, so
/// the tests exercise the shipped registration and the shipped type rather than a stand-in. No web
/// server, node process, or network is involved.
/// </para>
/// </summary>
internal static class SpaRouteTestHost
{
    /// <summary>The route table used by the demo app, and the one most tests assert against.</summary>
    public static void DemoRoutes(ISpaRouteBuilder routes) => routes
        .Route("", "home")
        .Group("person", "person", person => person
            .Route("", "list")
            .Route("create", "create")
            .Route("{personid}", "show")
            .Route("{personid}/edit", "edit")
            .Route("{personid}/{name}", "show-name")
            .Route("{personid}/{name}/edit", "edit-name")
        );

    public static ISpaRouteService Create(Action<ISpaRouteBuilder> buildRoutes)
        => Create(buildRoutes, out _);

    public static ISpaRouteService Create(Action<ISpaRouteBuilder> buildRoutes, out CountingPrerenderingService prerenderingService)
    {
        var service = new CountingPrerenderingService(buildRoutes);
        prerenderingService = service;

        var provider = new ServiceCollection()
            .AddSpaRouteServices()
            .AddSingleton<ISpaPrerenderingService>(service)
            .BuildServiceProvider();

        return provider.GetRequiredService<ISpaRouteService>();
    }

    /// <summary>
    /// Records how often the route table was built, so the caching in <c>EnsureSpaRoutesBuilt</c>
    /// can be asserted rather than assumed.
    /// </summary>
    internal sealed class CountingPrerenderingService(Action<ISpaRouteBuilder> buildRoutes) : ISpaPrerenderingService
    {
        private int buildCount;

        public int BuildCount => Volatile.Read(ref buildCount);

        public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
        {
            Interlocked.Increment(ref buildCount);
            buildRoutes(routeBuilder);
            return Task.CompletedTask;
        }

        public Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
            => Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="SpaRouteService"/> reads the request path by reflecting for a <c>RawTarget</c> property
/// on the feature collection's concrete type, because <c>Request.Path</c> returns <c>index.html</c>
/// for a prerendered SPA in Production. A stock <see cref="FeatureCollection"/> has no such property,
/// so a context built without this type throws <see cref="NullReferenceException"/>.
/// </summary>
internal sealed class RawTargetFeatureCollection : FeatureCollection
{
    public RawTargetFeatureCollection()
    {
        // DefaultHttpContext resolves Request/Response through these; a bare FeatureCollection has
        // neither, so touching Request.Scheme would throw before any library code runs.
        Set<IHttpRequestFeature>(new HttpRequestFeature());
        Set<IHttpResponseFeature>(new HttpResponseFeature());
    }

    public string RawTarget { get; set; } = "/";
}

internal static class HttpContextFactory
{
    public static DefaultHttpContext WithRawTarget(string rawTarget)
        => new(new RawTargetFeatureCollection { RawTarget = rawTarget });
}
