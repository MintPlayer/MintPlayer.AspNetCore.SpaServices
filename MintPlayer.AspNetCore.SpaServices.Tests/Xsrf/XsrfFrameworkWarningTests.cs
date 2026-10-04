using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.Assertions;
using System.Security.Cryptography;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Xsrf;

/// <summary>
/// Issue #88, part 1. <c>DefaultAntiforgery</c> logs "the 'Cache-Control' and 'Pragma' headers have
/// been overridden" (EventId 8) whenever the response already carries a caching policy - and under
/// <see cref="XsrfCacheHeaderPolicy.PreservePrivate"/> the package puts that policy straight back, so
/// the message describes headers the client never receives, once per request.
/// </summary>
/// <remarks>
/// These run the <em>real</em> <c>DefaultAntiforgery</c> with its logger routed into the recorder.
/// The stub never logs, so a "no Warning" assertion against it proves nothing.
/// </remarks>
public class XsrfFrameworkWarningTests
{
    private static bool IsFrameworkOverrideWarning(XsrfTestHost.Entry entry)
        => entry.EventId.Id == XsrfTestHost.CacheHeadersOverriddenEventId
            && entry.Category.EndsWith("DefaultAntiforgery", StringComparison.Ordinal);

    [Theory]
    [InlineData("public, max-age=300", "max-age=300, private")]
    [InlineData("private, max-age=300", "private, max-age=300")]
    [InlineData("no-store", "no-store, private")]
    // The framework wants no-cache AND no-store; no-cache alone still triggered the warning.
    [InlineData("no-cache", "no-cache, private")]
    public async Task Logs_no_warning_when_the_policy_is_preserved(string cacheControl, string expected)
    {
        var result = await XsrfTestHost.Run(frameworkAntiforgery: true, requests: 2,
            configureContext: context => context.Response.Headers.CacheControl = cacheControl);

        result.Logs.Should().NotContain(l => l.Level >= LogLevel.Warning);
        result.Context.Response.Headers.CacheControl.ToString().Should().Be(expected);
        result.SetCookies.Should().Contain(c => c.StartsWith("XSRF-TOKEN="));
    }

    [Fact]
    public async Task Logs_no_warning_for_a_pragma_set_on_its_own()
    {
        // A Pragma other than no-cache is the framework's second trigger.
        var result = await XsrfTestHost.Run(frameworkAntiforgery: true,
            configureContext: context => context.Response.Headers.Pragma = "public");

        result.Logs.Should().NotContain(l => l.Level >= LogLevel.Warning);
        result.Context.Response.Headers.Pragma.ToString().Should().Be("public");
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
    }

    [Fact]
    public async Task Leaves_a_response_without_a_policy_exactly_as_the_framework_stamps_it()
    {
        var result = await XsrfTestHost.Run(frameworkAntiforgery: true);

        result.Logs.Should().NotContain(l => l.Level >= LogLevel.Warning);
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
        result.Context.Response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task Still_logs_the_framework_warning_under_NoStore()
    {
        // The override is real under NoStore, so the warning is true and must stay. This is also
        // the assertion that proves the recorder sees the framework at all.
        var result = await XsrfTestHost.Run(frameworkAntiforgery: true,
            configure: options => options.CacheHeaders = XsrfCacheHeaderPolicy.NoStore,
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        result.Logs.Should().Contain(IsFrameworkOverrideWarning);
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
    }

    [Fact]
    public async Task Restores_the_application_policy_verbatim_when_the_mint_throws()
    {
        // The headers are hidden from the framework during the mint, so a throw would otherwise
        // leave the response with neither. No token reached the client, so there is nothing to make
        // private: the application's value comes back exactly as it set it.
        var result = await XsrfTestHost.Run(
            antiforgery: StubAntiforgery.Throwing(() => new CryptographicException("key ring")),
            configureContext: context =>
            {
                context.Response.Headers.CacheControl = "public, max-age=300";
                context.Response.Headers.Pragma = "custom";
            });

        result.Context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=300");
        result.Context.Response.Headers.Pragma.ToString().Should().Be("custom");
        result.SetCookies.Should().BeEmpty();
        result.Logs.Should().Contain(l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Forces_private_when_a_failed_mint_still_left_a_cookie_behind()
    {
        // A replaced IAntiforgery may write its cookie and then throw. A per-user Set-Cookie is now on
        // the response, so restoring "public" verbatim would let a shared cache hand it to the next user.
        var result = await XsrfTestHost.Run(
            antiforgery: StubAntiforgery.ThrowingAfterTheCookie(() => new InvalidOperationException("late")),
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        result.SetCookies.Should().NotBeEmpty();
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("max-age=300, private");
    }

    [Fact]
    public async Task Reinstates_nothing_when_a_failed_mint_had_no_policy_to_hide()
    {
        var result = await XsrfTestHost.Run(
            antiforgery: StubAntiforgery.Throwing(() => new CryptographicException("key ring")));

        result.Context.Response.Headers.ContainsKey("Cache-Control").Should().BeFalse();
        result.Context.Response.Headers.ContainsKey("Pragma").Should().BeFalse();
    }

    [Fact]
    public async Task Hides_nothing_under_NoStore_when_the_mint_throws()
    {
        var result = await XsrfTestHost.Run(
            configure: options => options.CacheHeaders = XsrfCacheHeaderPolicy.NoStore,
            antiforgery: StubAntiforgery.Throwing(() => new CryptographicException("key ring")),
            configureContext: context => context.Response.Headers.CacheControl = "public, max-age=300");

        result.Context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=300");
    }

    [Fact]
    public async Task Reports_a_dropped_unparseable_cache_control_once()
    {
        // The framework is silent about a value it cannot parse, so before rc.3 the application's
        // caching policy vanished without a word. The package now says so - once.
        var result = await XsrfTestHost.Run(frameworkAntiforgery: true, requests: 3,
            configureContext: context => context.Response.Headers.CacheControl = "max-age=\"unterminated");

        var warning = result.Logs.Should().ContainSingle(l => l.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("max-age=\"unterminated").And.Contain("once per process");
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
        result.Context.Response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task Writes_no_store_itself_when_a_replaced_antiforgery_stamps_nothing()
    {
        // The application's headers were hidden for the mint. An IAntiforgery that writes no cache
        // headers of its own must not leave an unrestorable policy with no Cache-Control at all.
        var result = await XsrfTestHost.Run(
            antiforgery: StubAntiforgery.StampingNoCacheHeaders(),
            configureContext: context => context.Response.Headers.CacheControl = "max-age=abc");

        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
        result.Context.Response.Headers.Pragma.ToString().Should().Be("no-cache");
    }
}
