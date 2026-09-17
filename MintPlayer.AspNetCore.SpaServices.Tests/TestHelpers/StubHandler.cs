using System.Net;

namespace MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;

/// <summary>
/// Stands in for the network. Every proxy test drives the real code path through this handler, so no
/// test ever opens a socket.
/// </summary>
/// <remarks>
/// Shared rather than private to one test class: the proxy middleware and the Angular CLI readiness
/// poll both need an outbound seam, and they are in different suites.
/// </remarks>
internal sealed class StubHandler : HttpMessageHandler
{
	private readonly Func<CancellationToken, HttpResponseMessage> respond;

	public StubHandler()
		: this(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") })
	{
	}

	public StubHandler(HttpResponseMessage response) : this(_ => response)
	{
	}

	public StubHandler(Func<CancellationToken, HttpResponseMessage> respond)
	{
		this.respond = respond;
	}

	public HttpRequestMessage? LastRequest { get; private set; }

	public string? LastRequestBody { get; private set; }

	/// <summary>Number of requests this handler has served.</summary>
	public int RequestCount { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		RequestCount++;
		LastRequest = request;
		if (request.Content is not null)
			LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

		return respond(cancellationToken);
	}
}
