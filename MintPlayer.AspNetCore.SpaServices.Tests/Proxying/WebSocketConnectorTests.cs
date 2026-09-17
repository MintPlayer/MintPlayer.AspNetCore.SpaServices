using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.SpaServices.Proxying;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Proxying;

/// <summary>
/// The websocket upgrade path, driven through a fake connector and a hand-built
/// <see cref="IHttpWebSocketFeature"/>. Nothing dials out, and no dev server is needed.
/// </summary>
public class WebSocketConnectorTests
{
	[Fact]
	public void Drops_the_handshake_headers_that_the_outbound_client_regenerates()
	{
		var headers = new HeaderDictionary
		{
			["Host"] = "localhost:5000",
			["Connection"] = "Upgrade",
			["Upgrade"] = "websocket",
			["Sec-WebSocket-Key"] = "abc",
			["Sec-WebSocket-Version"] = "13",
			["Sec-WebSocket-Protocol"] = "sockjs",
			["User-Agent"] = "Mozilla",
			["Accept"] = "*/*",
			["X-Custom"] = "keep me",
			["Cookie"] = "session=1",
		};

		var forwarded = SpaProxy.ForwardableWebSocketHeaders(headers).Select(h => h.Key).ToArray();

		// Forwarding the original handshake headers corrupts the new handshake the client performs.
		Assert.DoesNotContain("Host", forwarded);
		Assert.DoesNotContain("Connection", forwarded);
		Assert.DoesNotContain("Upgrade", forwarded);
		Assert.DoesNotContain("Sec-WebSocket-Key", forwarded);
		Assert.DoesNotContain("Sec-WebSocket-Version", forwarded);
		Assert.DoesNotContain("Sec-WebSocket-Protocol", forwarded);
		// User-Agent and Accept are dropped for aspnet/JavaScriptServices#1469.
		Assert.DoesNotContain("User-Agent", forwarded);
		Assert.DoesNotContain("Accept", forwarded);

		// Anything else is the application's business and must survive.
		Assert.Contains("X-Custom", forwarded);
		Assert.Contains("Cookie", forwarded);
	}

	[Fact]
	public void Header_filtering_is_case_insensitive()
	{
		var headers = new HeaderDictionary { ["host"] = "x", ["sec-websocket-key"] = "y" };

		Assert.Empty(SpaProxy.ForwardableWebSocketHeaders(headers));
	}

	[Fact]
	public async Task Connects_to_the_destination_and_pumps_both_directions()
	{
		var upstream = new WebSocketProxyTests.FakeWebSocket();
		upstream.Enqueue("from the dev server");
		upstream.EnqueueClose();
		var downstream = new WebSocketProxyTests.FakeWebSocket();
		downstream.EnqueueClose();
		var connector = new FakeConnector(upstream);
		var context = CreateWebSocketContext(downstream);

		var proxied = await SpaProxy.AcceptProxyWebSocketRequest(
			context, new Uri("ws://localhost:4200/sockjs"), CancellationToken.None, connector);

		Assert.True(proxied);
		Assert.Equal(new Uri("ws://localhost:4200/sockjs"), connector.LastDestination);
		// Frames from the dev server reach the browser socket.
		Assert.Equal(["from the dev server"], downstream.Sent);
	}

	[Fact]
	public async Task Forwards_the_requested_sub_protocols()
	{
		var connector = new FakeConnector(Closed());
		var context = CreateWebSocketContext(Closed(), subProtocols: ["sockjs", "v2.stomp"]);

		await SpaProxy.AcceptProxyWebSocketRequest(context, new Uri("ws://localhost:4200/"), CancellationToken.None, connector);

		Assert.Equal(["sockjs", "v2.stomp"], connector.LastSubProtocols);
	}

	[Fact]
	public async Task Answers_400_when_the_upstream_refuses_the_upgrade()
	{
		// The dev server is not listening yet. A 400 is the honest answer - anything else reads as if
		// the proxy itself had failed.
		var connector = new FakeConnector(new WebSocketException("refused"));
		var context = CreateWebSocketContext(Closed());

		var proxied = await SpaProxy.AcceptProxyWebSocketRequest(
			context, new Uri("ws://localhost:4200/"), CancellationToken.None, connector);

		Assert.False(proxied);
		Assert.Equal(400, context.Response.StatusCode);
	}

	[Fact]
	public async Task Rejects_a_null_context()
	{
		await Assert.ThrowsAsync<ArgumentNullException>(
			() => SpaProxy.AcceptProxyWebSocketRequest(null!, new Uri("ws://localhost:4200/"), CancellationToken.None, new FakeConnector(Closed())));
	}

	[Fact]
	public async Task Rejects_a_null_destination()
	{
		var context = CreateWebSocketContext(Closed());

		await Assert.ThrowsAsync<ArgumentNullException>(
			() => SpaProxy.AcceptProxyWebSocketRequest(context, null!, CancellationToken.None, new FakeConnector(Closed())));
	}

	private static WebSocketProxyTests.FakeWebSocket Closed()
	{
		var socket = new WebSocketProxyTests.FakeWebSocket();
		socket.EnqueueClose();
		return socket;
	}

	private static DefaultHttpContext CreateWebSocketContext(WebSocket accepted, string[]? subProtocols = null)
	{
		var requestHeaders = new HeaderDictionary();
		if (subProtocols is { Length: > 0 })
		{
			// WebSocketManager parses the requested protocols out of this header rather than reading
			// them off the feature, so this is what the proxy actually sees.
			requestHeaders["Sec-WebSocket-Protocol"] = string.Join(", ", subProtocols);
		}

		var features = new FeatureCollection();
		features.Set<IHttpRequestFeature>(new HttpRequestFeature { Method = "GET", Path = "/", Scheme = "http", Headers = requestHeaders });
		features.Set<IHttpResponseFeature>(new HttpResponseFeature());
		features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
		features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(accepted, subProtocols ?? []));

		return new DefaultHttpContext(features);
	}

	private sealed class FakeWebSocketFeature(WebSocket accepted, string[] subProtocols) : IHttpWebSocketFeature
	{
		public bool IsWebSocketRequest => true;

		public string? AcceptedSubProtocol { get; private set; }

		public IList<string> RequestedProtocols { get; } = subProtocols;

		public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
		{
			AcceptedSubProtocol = context.SubProtocol;
			return Task.FromResult(accepted);
		}
	}

	private sealed class FakeConnector : IWebSocketConnector
	{
		private readonly WebSocket? socket;
		private readonly Exception? failure;

		public FakeConnector(WebSocket socket) => this.socket = socket;

		public FakeConnector(Exception failure) => this.failure = failure;

		public Uri? LastDestination { get; private set; }

		public string[]? LastSubProtocols { get; private set; }

		public Task<WebSocket> ConnectAsync(Uri destination, IEnumerable<string> subProtocols, IEnumerable<KeyValuePair<string, StringValues>> headers, CancellationToken cancellationToken)
		{
			LastDestination = destination;
			LastSubProtocols = subProtocols.ToArray();

			if (failure is not null)
				throw failure;

			return Task.FromResult(socket!);
		}
	}
}
