using MintPlayer.AspNetCore.SpaServices.Routing.Exceptions;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Routing;

public class GenerateUrlTests
{
    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    [Theory]
    [InlineData("home", "/")]
    [InlineData("person-list", "/person")]
    [InlineData("person-create", "/person/create")]
    [InlineData("person", "/person")]
    public async Task Generates_parameterless_routes(string routeName, string expected)
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        Assert.Equal(expected, await service.GenerateUrl(routeName, Params()));
    }

    [Fact]
    public async Task Substitutes_a_single_parameter()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        Assert.Equal("/person/5/edit", await service.GenerateUrl("person-edit", Params(("personid", 5))));
    }

    [Fact]
    public async Task Substitutes_every_parameter_in_a_multi_parameter_route()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-edit-name", Params(("personid", 5), ("name", "john-doe")));

        Assert.Equal("/person/5/john-doe/edit", url);
    }

    [Fact]
    public async Task Appends_parameters_the_route_does_not_declare_as_a_query_string()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-show", Params(("personid", 5), ("tab", "info")));

        Assert.Equal("/person/5?tab=info", url);
    }

    [Fact]
    public async Task Joins_multiple_excess_parameters_with_ampersands()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-list", Params(("a", 1), ("b", 2)));

        Assert.Equal("/person?a=1&b=2", url);
    }

    [Fact]
    public async Task Reads_parameters_from_an_anonymous_object()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        Assert.Equal("/person/5/edit", await service.GenerateUrl("person-edit", new { personid = 5 }));
    }

    [Fact]
    public async Task Uses_the_static_type_when_reading_parameters_from_an_object()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        // The generic overload reflects over typeof(T), not parameters.GetType(). Boxing to object
        // therefore yields no properties at all, and the placeholder has nothing to substitute.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GenerateUrl<object>("person-show", new { personid = 5 }));
    }

    [Fact]
    public async Task Throws_SpaRouteNotFoundException_for_an_unknown_route()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var ex = await Assert.ThrowsAsync<SpaRouteNotFoundException>(
            () => service.GenerateUrl("nope", Params()));

        Assert.Equal("Route with name nope not found.", ex.Message);
    }

    [Fact]
    public async Task Route_lookup_is_case_sensitive()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await Assert.ThrowsAsync<SpaRouteNotFoundException>(
            () => service.GenerateUrl("Person-Edit", Params(("personid", 5))));
    }

    [Fact]
    public async Task Throws_KeyNotFoundException_when_a_declared_parameter_is_missing()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GenerateUrl("person-show", Params()));
    }

    [Fact]
    public async Task Prefixes_scheme_host_and_path_base_from_an_HttpContext()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);
        var context = HttpContextFactory.WithRawTarget("/");
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost:5001");
        context.Request.PathBase = "/app";

        var url = await service.GenerateUrl("person-edit", Params(("personid", 5)), context);

        Assert.Equal("https://localhost:5001/app/person/5/edit", url);
    }

    [Fact]
    public async Task Prefixes_an_explicit_protocol_and_host_without_a_path_base()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-edit", Params(("personid", 5)), "https", "example.com");

        Assert.Equal("https://example.com/person/5/edit", url);
    }

    [Fact]
    public async Task Appends_a_fragment_after_the_path()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-show", Params(("personid", 5)), "https", "example.com", "details");

        Assert.Equal("https://example.com/person/5#details", url);
    }

    [Fact]
    public async Task Appends_a_bare_hash_for_an_empty_fragment()
    {
        // Current behaviour: the '#' is appended unconditionally, so an empty fragment leaves a
        // trailing separator. Pinned so that adding a guard is a deliberate, visible change.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-show", Params(("personid", 5)), "https", "example.com", "");

        Assert.Equal("https://example.com/person/5#", url);
    }

    [Fact]
    public async Task Does_not_url_encode_parameter_values()
    {
        // Current behaviour: values are interpolated verbatim, so a value containing a separator
        // silently changes the shape of the URL. System.Net is imported by the service but never
        // used. Pinned as documentation of a real escaping gap, not as an endorsement.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-show", Params(("personid", "a b&c")));

        Assert.Equal("/person/a b&c", url);
    }

    [Fact]
    public async Task Builds_the_route_table_only_once_across_many_calls()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes, out var prerendering);

        for (var i = 0; i < 5; i++)
            await service.GenerateUrl("home", Params());

        Assert.Equal(1, prerendering.BuildCount);
    }
}
