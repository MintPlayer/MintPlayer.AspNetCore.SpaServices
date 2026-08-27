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
    public async Task Discards_the_query_string_on_the_empty_route()
    {
        // Current behaviour, and almost certainly a bug: the branch handling a route whose path is
        // empty returns before the query is parsed, so /?a=b loses its query while every other
        // route keeps it. Pinned so a fix is deliberate.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/?a=b"));

        Assert.Equal("home", route!.Name);
        Assert.Empty(route.QueryParameters);
    }

    [Fact]
    public async Task Throws_on_a_duplicated_query_key()
    {
        // Current behaviour: query parameters land in a ToDictionary with no duplicate handling, so
        // a repeated key - which is legal in a URL - throws rather than taking first or last.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a=1&a=2")));
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
    public async Task Splits_the_query_at_the_last_question_mark()
    {
        // The split is LastIndexOf('?'), not IndexOf, so "/person/5?a=b?c" becomes path
        // "/person/5?a=b" and query "c". A route segment is matched with [^/]+, which happily
        // accepts '?', so the leftover query lands *inside* the route parameter instead of being
        // parsed as one. Pinned as documentation of the current, surprising behaviour.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/5?a=b?c"));

        Assert.Equal("person-show", route!.Name);
        Assert.Equal("5?a=b", route.Parameters["personid"]);
        Assert.True(route.QueryParameters.ContainsKey("c"));
    }

    [Fact]
    public async Task Does_not_escape_regex_metacharacters_in_a_route_path()
    {
        // Current behaviour: a route path is interpolated straight into a Regex, so '.' is a
        // wildcard rather than a literal dot. Pinned as documentation of a real gap.
        var service = SpaRouteTestHost.Create(routes => routes.Route("a.b", "dotted"));

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/axb"));

        Assert.Equal("dotted", route!.Name);
    }

    [Fact]
    public async Task Does_not_decode_percent_escapes()
    {
        // The raw target is read verbatim and never decoded, so a percent-escaped space stays
        // escaped in the extracted parameter. Combined with GenerateUrl not encoding, a
        // generate/parse round-trip is lossy in both directions.
        var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

        var route = await service.GetCurrentRoute(HttpContextFactory.WithRawTarget("/person/a%20b"));

        Assert.Equal("a%20b", route!.Parameters["personid"]);
    }
}
