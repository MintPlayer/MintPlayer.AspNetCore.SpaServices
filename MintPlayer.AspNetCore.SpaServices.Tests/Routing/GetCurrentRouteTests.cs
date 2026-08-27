using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Routing;

public class GetCurrentRouteTests
{
    [Theory]
    [InlineData("/", "home")]
    [InlineData("/person/create", "person-create")]
    [InlineData("/person/5", "person-show")]
    [InlineData("/person/5/edit", "person-edit")]
    [InlineData("/person/5/john-doe", "person-show-name")]
    [InlineData("/person/5/john-doe/edit", "person-edit-name")]
    public async Task Matches_the_expected_route(string rawTarget, string expectedName)
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget(rawTarget));

        Assert.NotNull(route);
        Assert.Equal(expectedName, route!.Name);
    }

    [Fact]
    public async Task Prefers_the_nested_empty_route_over_the_group_that_contains_it()
    {
        // Route("", "list") inside Group("person", ...) produces the same FullPath as the group node
        // itself. Both match /person; the flatten order (descendants before their parent) is the only
        // thing that decides. Pinned because that ordering is load-bearing and easy to break.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person"));

        Assert.Equal("person-list", route!.Name);
    }

    [Fact]
    public async Task Prefers_a_literal_segment_over_a_parameter_that_would_also_match()
    {
        // /person/create matches both "person/create" and "person/{personid}". Declaration order
        // within a level decides, so the literal wins only because it is declared first.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/create"));

        Assert.Equal("person-create", route!.Name);
    }

    [Fact]
    public async Task Extracts_route_parameters_positionally()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5/john-doe"));

        Assert.Equal("5", route!.Parameters["personid"]);
        Assert.Equal("john-doe", route.Parameters["name"]);
    }

    [Fact]
    public async Task Parses_the_query_string()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?tab=info&x=1"));

        Assert.Equal("5", route!.Parameters["personid"]);
        Assert.Equal("info", route.QueryParameters["tab"]);
        Assert.Equal("1", route.QueryParameters["x"]);
    }

    [Fact]
    public async Task Maps_a_value_less_query_parameter_to_null()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?flag"));

        Assert.True(route!.QueryParameters.ContainsKey("flag"));
        Assert.Null(route.QueryParameters["flag"]);
    }

    [Fact]
    public async Task Keeps_everything_after_the_first_equals_sign_as_the_value()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a=b=c"));

        Assert.Equal("b=c", route!.QueryParameters["a"]);
    }

    [Fact]
    public async Task Parses_the_query_string_on_the_empty_route_too()
    {
        // The branch handling a route whose path is empty used to return before parsing the query,
        // so /?a=b lost its query while every other route kept it.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/?a=b"));

        Assert.Equal("home", route!.Name);
        Assert.Equal("b", route.QueryParameters["a"]);
    }

    [Fact]
    public async Task Returns_no_query_parameters_for_the_empty_route_without_a_query()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/"));

        Assert.Empty(route!.QueryParameters);
    }

    [Fact]
    public async Task Takes_the_last_value_of_a_duplicated_query_key()
    {
        // A repeated key is legal in a URL. It used to reach a ToDictionary with no duplicate
        // handling and throw, taking the whole request down rather than picking a value.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a=1&a=2"));

        Assert.Equal("2", route!.QueryParameters["a"]);
    }

    [Theory]
    [InlineData("/person/")]
    [InlineData("/nothing")]
    [InlineData("/Person")]
    public async Task Returns_null_when_nothing_matches(string rawTarget)
    {
        // Includes a trailing slash (segments are matched with '+', so they cannot be empty) and a
        // casing difference (matching uses no IgnoreCase option).
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        Assert.Null(await service.GetCurrentRoute(HttpContextFactory.WithRawTarget(rawTarget)));
    }

    [Fact]
    public async Task Splits_the_query_at_the_first_question_mark()
    {
        // The query starts at the first '?' (RFC 3986 3.4); any later one is part of the query. The
        // split used to be LastIndexOf, which left "?a=b" inside the path where it was captured as
        // part of the route parameter.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a=b?c"));

        Assert.Equal("person-show", route!.Name);
        Assert.Equal("5", route.Parameters["personid"]);
        Assert.Equal("b?c", route.QueryParameters["a"]);
    }

    [Fact]
    public async Task Treats_a_dot_in_a_route_path_as_a_literal()
    {
        // A route path used to be interpolated straight into a Regex, so '.' matched any character
        // and the route "a.b" also answered "/axb".
        var service = SpaRouteTestHost.Create(routes => routes.Route("a.b", "dotted"));

        Assert.Null(await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/axb")));
        Assert.Equal("dotted", (await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/a.b")))!.Name);
    }

    [Fact]
    public async Task Matches_a_route_path_containing_regex_metacharacters()
    {
        // An unescaped '(' is an unterminated group and used to throw at match time rather than
        // simply not matching.
        var service = SpaRouteTestHost.Create(routes => routes.Route("report(2026)", "report"));

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/report(2026)"));

        Assert.Equal("report", route!.Name);
    }

    [Fact]
    public async Task Decodes_percent_escapes_in_route_parameters()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/a%20b"));

        Assert.Equal("a b", route!.Parameters["personid"]);
    }

    [Fact]
    public async Task Decodes_percent_escapes_in_query_parameters()
    {
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a%20b=c%26d"));

        Assert.Equal("c&d", route!.QueryParameters["a b"]);
    }

    [Fact]
    public async Task Leaves_a_plus_sign_alone_when_decoding()
    {
        // GenerateUrl encodes a space as %20, so the round-trip is symmetric without reading '+' as
        // a space - and doing so would corrupt a value that legitimately contains one.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/a+b"));

        Assert.Equal("a+b", route!.Parameters["personid"]);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a&b")]
    [InlineData("a?b")]
    [InlineData("a%b")]
    public async Task Round_trips_a_value_through_generate_and_parse(string value)
    {
        // The point of encoding on the way out and decoding on the way back: whatever a caller puts
        // in comes back out unchanged, whichever separators it happens to contain.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var url = await service.GenerateUrl("person-show", new Dictionary<string, object> { ["personid"] = value });
        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget(url));

        Assert.Equal("person-show", route!.Name);
        Assert.Equal(value, route.Parameters["personid"]);
    }
}
