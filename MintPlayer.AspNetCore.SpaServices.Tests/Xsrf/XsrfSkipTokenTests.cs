using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.Assertions;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

/// <summary>
/// Issue #88, part 2: keeping one response out of the mint without taking it out of the pipeline.
/// See <see cref="XsrfSkipTokenPipelineTests"/> for the same claims measured on real Kestrel.
/// </summary>
public class XsrfSkipTokenTests
{
    private sealed class OwnSkipMetadata : ISkipXsrfTokenMetadata
    {
        public bool Skip => true;
    }

    private static void SetEndpoint(HttpContext context, params object[] metadata)
        => context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));

    /// <summary>
    /// Mirrors the real-world sequence: the generator registers its callback, then routing (further
    /// down the pipeline) selects the endpoint, then the response starts.
    /// </summary>
    private static Task<XsrfTestHost.Result> RunWithEndpoint(Action<XsrfOptions>? configure = null, params object[] metadata)
        => XsrfTestHost.Run(
            configure: configure,
            next: context =>
            {
                SetEndpoint(context, metadata);
                context.Response.Headers.CacheControl = "public, max-age=300";
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Skips_the_mint_for_an_endpoint_carrying_the_attribute()
    {
        var result = await RunWithEndpoint(metadata: new SkipXsrfTokenAttribute());

        result.SetCookies.Should().BeEmpty();
        // Untouched: not forced private, no Pragma, and none of the mint's other side effects.
        var headers = result.Context.Response.Headers;
        headers.CacheControl.ToString().Should().Be("public, max-age=300");
        headers.ContainsKey("Pragma").Should().BeFalse();
        headers.ContainsKey("X-Frame-Options").Should().BeFalse();
        result.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_the_mint_for_application_supplied_metadata()
    {
        var result = await RunWithEndpoint(metadata: new OwnSkipMetadata());

        result.SetCookies.Should().BeEmpty();
    }

    [Fact]
    public async Task Mints_for_an_endpoint_without_the_metadata()
    {
        var result = await RunWithEndpoint(metadata: new object());

        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("max-age=300, private");
    }

    [Fact]
    public async Task Mints_for_a_response_with_no_endpoint()
    {
        // A response that starts before routing ran, or matched no route, behaves as before.
        var result = await XsrfTestHost.Run();

        result.Context.GetEndpoint().Should().BeNull();
        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
    }

    [Fact]
    public async Task Skips_the_mint_when_ShouldIssue_returns_false()
    {
        HttpContext? seen = null;
        var result = await XsrfTestHost.Run(configure: options => options.ShouldIssue = context =>
        {
            seen = context;
            return false;
        });

        result.SetCookies.Should().BeEmpty();
        seen.Should().BeSameAs(result.Context);
        result.Context.Response.Headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Fact]
    public async Task Mints_when_ShouldIssue_returns_true()
    {
        var result = await XsrfTestHost.Run(configure: options => options.ShouldIssue = _ => true);

        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
    }

    [Fact]
    public async Task ShouldIssue_runs_when_the_response_starts_so_it_sees_the_endpoint_and_status()
    {
        Endpoint? endpoint = null;
        var status = 0;
        await XsrfTestHost.Run(
            configure: options => options.ShouldIssue = context =>
            {
                endpoint = context.GetEndpoint();
                status = context.Response.StatusCode;
                return true;
            },
            next: context =>
            {
                SetEndpoint(context);
                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            });

        endpoint.Should().NotBeNull();
        status.Should().Be(404);
    }

    [Fact]
    public async Task Endpoint_metadata_wins_without_consulting_ShouldIssue()
    {
        var called = false;
        var result = await RunWithEndpoint(
            configure: options => options.ShouldIssue = _ => called = true,
            metadata: new SkipXsrfTokenAttribute());

        result.SetCookies.Should().BeEmpty();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task A_throwing_ShouldIssue_is_logged_and_fails_open_by_issuing_the_token()
    {
        // Wrongly minting costs a private downgrade; wrongly skipping the SPA's entry point breaks
        // every mutation after it. So a broken predicate mints.
        var result = await XsrfTestHost.Run(
            configure: options => options.ShouldIssue = _ => throw new InvalidOperationException("predicate bug"),
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("max-age=300, private");
        var error = result.Logs.Should().ContainSingle(l => l.Level == LogLevel.Error).Which;
        error.Exception.Should().BeOfType<InvalidOperationException>();
        error.Message.Should().Contain(nameof(XsrfOptions.ShouldIssue));
    }

    [Fact]
    public async Task The_most_specific_metadata_wins_so_Skip_false_re_enables_the_mint()
    {
        // Metadata is ordered least to most specific: the group's (or controller's) first, then
        // the endpoint's (or action's) own.
        var result = await RunWithEndpoint(metadata: [new SkipXsrfTokenAttribute(), new SkipXsrfTokenAttribute(skip: false)]);

        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
    }

    [Fact]
    public async Task A_more_specific_skip_overrides_an_outer_re_enable()
    {
        var result = await RunWithEndpoint(metadata: [new SkipXsrfTokenAttribute(skip: false), new SkipXsrfTokenAttribute()]);

        result.SetCookies.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_Skip_false_metadata_mints_without_consulting_ShouldIssue()
    {
        var called = false;
        var result = await RunWithEndpoint(
            configure: options => options.ShouldIssue = _ => !(called = true),
            metadata: new SkipXsrfTokenAttribute(skip: false));

        result.XsrfCookie.Should().StartWith($"XSRF-TOKEN={XsrfTestHost.RequestToken}");
        called.Should().BeFalse();
    }

    [Fact]
    public void The_attribute_skips_by_default()
    {
        new SkipXsrfTokenAttribute().Skip.Should().BeTrue();
        new SkipXsrfTokenAttribute(skip: false).Skip.Should().BeFalse();
    }

    [Fact]
    public void ShouldIssue_defaults_to_null()
        => new XsrfOptions().ShouldIssue.Should().BeNull();

    [Fact]
    public void SkipXsrfToken_adds_the_attribute_and_returns_the_same_builder()
    {
        var builder = new TestConventionBuilder();

        var returned = builder.SkipXsrfToken();

        returned.Should().BeSameAs(builder);
        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/"), 0);
        foreach (var convention in builder.Conventions)
        {
            convention(endpointBuilder);
        }

        endpointBuilder.Metadata.Should().Contain(m => m is SkipXsrfTokenAttribute { Skip: true });
    }

    [Fact]
    public void SkipXsrfToken_false_adds_a_re_enabling_attribute()
    {
        var builder = new TestConventionBuilder();

        builder.SkipXsrfToken(skip: false);

        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/"), 0);
        builder.Conventions.Single()(endpointBuilder);
        endpointBuilder.Metadata.Should().Contain(m => m is SkipXsrfTokenAttribute { Skip: false });
    }

    [Fact]
    public void SkipXsrfToken_rejects_a_null_builder()
    {
        var act = () => ((TestConventionBuilder)null!).SkipXsrfToken();

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Fact]
    public void The_attribute_is_the_metadata_and_applies_to_classes_and_methods()
    {
        new SkipXsrfTokenAttribute().Should().BeAssignableTo<ISkipXsrfTokenMetadata>();

        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(SkipXsrfTokenAttribute), typeof(AttributeUsageAttribute))!;
        usage.ValidOn.Should().Be(AttributeTargets.Class | AttributeTargets.Method);
    }

    private sealed class TestConventionBuilder : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }
}
