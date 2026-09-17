using System.Net.WebSockets;
using Microsoft.Extensions.Primitives;

namespace MintPlayer.AspNetCore.SpaServices.Proxying;

/// <summary>
/// Opens the outbound half of a proxied websocket. Exists so the proxy can be driven without a dev
/// server listening: <c>ClientWebSocket</c> is sealed and always dials a real socket.
/// </summary>
internal interface IWebSocketConnector
{
	Task<WebSocket> ConnectAsync(
		Uri destination,
		IEnumerable<string> subProtocols,
		IEnumerable<KeyValuePair<string, StringValues>> headers,
		CancellationToken cancellationToken);
}

/// <summary>The shipped connector, backed by a real <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketConnector : IWebSocketConnector
{
	public static readonly ClientWebSocketConnector Instance = new();

	public async Task<WebSocket> ConnectAsync(
		Uri destination,
		IEnumerable<string> subProtocols,
		IEnumerable<KeyValuePair<string, StringValues>> headers,
		CancellationToken cancellationToken)
	{
		var client = new ClientWebSocket();
		try
		{
			foreach (var protocol in subProtocols)
			{
				client.Options.AddSubProtocol(protocol);
			}

			foreach (var headerEntry in headers)
			{
				try
				{
					client.Options.SetRequestHeader(headerEntry.Key, headerEntry.Value);
				}
				catch (ArgumentException)
				{
					// Certain header names are reserved and can't be set. The known ones are filtered
					// out before we get here, but a client can set arbitrary others. It's not helpful
					// to treat that as an error, so skip the non-forwardable ones. The perf
					// implications of a catch aren't an issue - this is a dev-time only feature.
				}
			}

			// Note that this is not really good enough to make Websockets work with
			// Angular CLI middleware. For some reason, ConnectAsync takes over 1 second,
			// on Windows, by which time the logic in SockJS has already timed out and made
			// it fall back on some other transport (xhr_streaming, usually). It's fine
			// on Linux though, completing almost instantly.
			//
			// The slowness on Windows does not cause a problem though, because the transport
			// fallback logic works correctly and doesn't surface any errors, but it would be
			// better if ConnectAsync was fast enough and the initial Websocket transport
			// could actually be used.
			await client.ConnectAsync(destination, cancellationToken);
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}
}
