using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Routing;

public class RedirectTests
{
    [Fact]
    public async Task Sets_the_location_header_to_the_generated_url()
    {
        var (context, features) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-edit", new Dictionary<string, object> { ["personid"] = 5 });
        await features.ResponseFeature.FireOnStartingAsync();

        Assert.Equal("/person/5/edit", context.Response.Headers.Location);
    }

    [Fact]
    public async Task Accepts_parameters_from_an_anonymous_object()
    {
        var (context, features) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-edit", new { personid = 5 });
        await features.ResponseFeature.FireOnStartingAsync();

        Assert.Equal("/person/5/edit", context.Response.Headers.Location);
    }

    [Fact]
    public async Task Sends_a_permanent_redirect()
    {
        // This used to assign StatusCode = 301 up front and then let Response.Redirect(url) - which
        // defaults to 302 - overwrite it from the OnStarting callback, so the permanent redirect the
        // code read as intending was never actually sent.
        var (context, features) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-show", new Dictionary<string, object> { ["personid"] = 5 });
        await features.ResponseFeature.FireOnStartingAsync();

        Assert.Equal(301, context.Response.StatusCode);
        Assert.Equal("/person/5", context.Response.Headers.Location);
    }

    [Fact]
    public async Task Sends_a_permanent_redirect_for_the_generic_overload_too()
    {
        var (context, features) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-show", new { personid = 5 });
        await features.ResponseFeature.FireOnStartingAsync();

        Assert.Equal(301, context.Response.StatusCode);
    }

    [Fact]
    public async Task Writes_the_redirect_immediately_rather_than_deferring_it()
    {
        var (context, _) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-show", new Dictionary<string, object> { ["personid"] = 5 });

        // This used to assert the opposite. The redirect was deferred to an OnStarting callback
        // because ServePrerenderResult called Response.Clear() and would otherwise have discarded
        // it - and because a deferred status is invisible to the prerender gate, Redirect also had
        // to call SkipPrerendering(). Neither is needed now, so the redirect is assigned directly
        // and the gate sees it straight away. See issue #81.
        Assert.Equal("/person/5", context.Response.Headers.Location);
        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
    }

    private static (DefaultHttpContext Context, RedirectFeatureCollection Features) CreateContext()
    {
        var features = new RedirectFeatureCollection();
        return (new DefaultHttpContext(features), features);
    }

    /// <summary>
    /// A stock response feature accepts <c>OnStarting</c> callbacks and discards them, which would
    /// make every assertion here pass without the redirect ever being written.
    /// </summary>
    private sealed class RedirectFeatureCollection : FeatureCollection
    {
        public RunnableResponseFeature ResponseFeature { get; } = new();

        public RedirectFeatureCollection()
        {
            Set<IHttpRequestFeature>(new HttpRequestFeature());
            Set<IHttpResponseFeature>(ResponseFeature);
            Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        }
    }

    private sealed class RunnableResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state)
            => callbacks.Add((callback, state));

        public async Task FireOnStartingAsync()
        {
            foreach (var (callback, state) in callbacks)
                await callback(state);
        }
    }
}
