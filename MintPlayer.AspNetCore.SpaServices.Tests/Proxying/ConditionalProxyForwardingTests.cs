using System.Net;
using MintPlayer.AspNetCore.SpaServices.Extensions.Proxy;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Proxying;

/// <summary>
/// What <c>ConditionalProxyMiddleware</c> does once it has decided a request is in scope. The
/// existing suite covers the routing decision; this covers the two outcomes after it - the request
/// was proxied, or it was not and the pipeline continues.
/// </summary>
public class ConditionalProxyForwardingTests
{
	[Fact]
	public async Task Does_not_call_next_when_the_request_was_proxied()
	{
		using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from the dev server") });
		var nextWasCalled = false;
		var middleware = CreateMiddleware("/", handler, _ => { nextWasCalled = true; return Task.CompletedTask; });
		var context = PerformProxyRequestTests.CreateContext(path: "/main.js");

		await middleware.Invoke(context);

		// The proxy answered, so the rest of the pipeline must not also run - that would write the
		// response twice.
		Assert.False(nextWasCalled);
		Assert.Equal("from the dev server", PerformProxyRequestTests.ReadResponseBody(context));
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task Calls_next_when_the_upstream_returns_404()
	{
		// The middleware proxies with proxy404s: false, so a 404 from the dev server means "not mine"
		// and the request falls through to the rest of the pipeline.
		using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
		var nextWasCalled = false;
		var middleware = CreateMiddleware("/", handler, _ => { nextWasCalled = true; return Task.CompletedTask; });
		var context = PerformProxyRequestTests.CreateContext(path: "/not-a-dev-server-asset");

		await middleware.Invoke(context);

		Assert.True(nextWasCalled);
	}

	[Fact]
	public async Task Skips_proxying_entirely_when_the_path_is_out_of_scope()
	{
		using var handler = new StubHandler();
		var nextWasCalled = false;
		var middleware = CreateMiddleware("/dist", handler, _ => { nextWasCalled = true; return Task.CompletedTask; });
		var context = PerformProxyRequestTests.CreateContext(path: "/api/people");

		await middleware.Invoke(context);

		Assert.True(nextWasCalled);
		// Nothing should have gone out on the wire at all.
		Assert.Equal(0, handler.RequestCount);
	}

	private static ConditionalProxyMiddleware CreateMiddleware(string pathPrefix, HttpMessageHandler handler, Microsoft.AspNetCore.Http.RequestDelegate next)
		=> new(
			next,
			pathPrefix,
			Task.FromResult(new Uri("http://localhost:4200/")),
			new HttpClient(handler),
			CancellationToken.None);
}
