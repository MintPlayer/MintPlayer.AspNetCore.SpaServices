using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// <c>SpaPrerenderingOptions.ExcludeUrls</c>, and the multi-value arm of the Content-Encoding gate.
/// Both run through the real middleware via <see cref="PrerenderingHarness"/>; no node is involved.
/// </summary>
public class ExcludedUrlsTests
{
    private const string IndexHtml =
        "<!doctype html><html><head><title>t</title></head><body><app-root></app-root></body></html>";

    [Theory]
    [InlineData("/dist")]
    [InlineData("/dist/main.css")]
    [InlineData("/assets/logo.svg")]
    public async Task Excluded_path_skips_prerendering(string path)
    {
        // Static assets under an excluded prefix must reach the client exactly as the inner
        // pipeline wrote them - never replaced by a prerendered index.html.
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: path,
            configureContext: context => context.Request.Path = path,
            configureOptions: options => options.ExcludeUrls = ["/assets", "/dist"]);

        Assert.False(result.Service.WasCalled);
        Assert.Equal(IndexHtml, Encoding.UTF8.GetString(result.ClientBody.ToArray()));
    }

    [Fact]
    public async Task A_path_that_only_shares_a_prefix_with_an_excluded_url_is_still_prerendered()
    {
        // The match is on whole segments: "/dist" must not swallow "/distribution".
        var result = await PrerenderingHarness.Run(
            PrerenderingHarness.HtmlPage(IndexHtml),
            rawTarget: "/distribution",
            configureContext: context => context.Request.Path = "/distribution",
            configureOptions: options => options.ExcludeUrls = ["/dist"]);

        Assert.True(result.Service.WasCalled);
    }

    [Fact]
    public async Task Does_not_prerender_a_capture_with_several_content_encodings()
    {
        // Even when every listed coding is identity, a multi-value header means the capture went
        // through more than one coding step, which this middleware cannot vouch for.
        var body = Encoding.UTF8.GetBytes(IndexHtml);

        var result = await PrerenderingHarness.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html";
            context.Response.Headers[HeaderNames.ContentEncoding] = new StringValues(["identity", "identity"]);
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        });

        Assert.False(result.Service.WasCalled);
        Assert.Equal(body, result.ClientBody.ToArray());
    }
}
