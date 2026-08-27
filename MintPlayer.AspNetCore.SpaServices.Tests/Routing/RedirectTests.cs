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
    public async Task Sends_302_even_though_301_was_assigned()
    {
        // Current behaviour, and a real bug. The method assigns StatusCode = 301 (Moved Permanently)
        // and then registers an OnStarting callback that calls Response.Redirect(url) - which sets
        // 302 (Found) itself, overwriting the 301. So the permanent redirect the code reads as
        // intending is never actually sent.
        //
        // Pinned rather than fixed: changing a redirect's permanence is a behavioural change for
        // consumers and belongs in its own commit, with its own reasoning. When that happens, this
        // test should fail and be updated to assert 301.
        var (context, features) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-show", new Dictionary<string, object> { ["personid"] = 5 });

        Assert.Equal(301, context.Response.StatusCode);

        await features.ResponseFeature.FireOnStartingAsync();

        Assert.Equal(302, context.Response.StatusCode);
    }

    [Fact]
    public async Task Does_not_touch_the_response_until_it_starts()
    {
        var (context, _) = CreateContext();
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await service.Redirect(context, "person-show", new Dictionary<string, object> { ["personid"] = 5 });

        Assert.False(context.Response.Headers.ContainsKey("Location"));
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
