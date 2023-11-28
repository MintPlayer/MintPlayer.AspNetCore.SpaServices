// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.WebSockets;

namespace MintPlayer.AspNetCore.SpaServices.Proxying;

// This duplicates and updates the proxying logic in SpaServices so that we can update
// the project templates without waiting for 2.1 to ship. When 2.1 is ready to ship,
// remove the old ConditionalProxy.cs from SpaServices and replace its usages with this.
// Doesn't affect public API surface - it's all internal.
internal static class SpaProxy
{
	private const int DefaultWebSocketBufferSize = 4096;
	private const int StreamCopyBufferSize = 81920;

	// https://github.com/dotnet/aspnetcore/issues/16797
	private static readonly HashSet<string> NotForwardedHttpHeaders = new HashSet<string>(
		new[] { "Connection" },
		StringComparer.OrdinalIgnoreCase
	);

	// Don't forward User-Agent/Accept because of https://github.com/aspnet/JavaScriptServices/issues/1469
	// Others just aren't applicable in proxy scenarios
	private static readonly HashSet<string> NotForwardedWebSocketHeaders = new HashSet<string>(
		new[] { "Accept", "Connection", "Host", "User-Agent", "Upgrade", "Sec-WebSocket-Key", "Sec-WebSocket-Protocol", "Sec-WebSocket-Version" },
		StringComparer.OrdinalIgnoreCase
	);

	// In case the connection to the client is HTTP/2 or HTTP/3 and to the server HTTP/1.1 or less, let's get rid of the HTTP/1.1 only headers
	private static readonly HashSet<string> InvalidH2H3Headers = new HashSet<string>(
		new[] { "Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection" },
		StringComparer.OrdinalIgnoreCase
	);

	public static HttpClient CreateHttpClientForProxy(TimeSpan requestTimeout)
	{
		var handler = new HttpClientHandler
		{
			AllowAutoRedirect = false,
			UseCookies = false,
		};

		return new HttpClient(handler)
		{
			Timeout = requestTimeout
		};
	}

	public static async Task<bool> PerformProxyRequest(
		HttpContext context,
		HttpClient httpClient,
		Task<Uri> baseUriTask,
		CancellationToken applicationStoppingToken,
		bool proxy404s)
	{
		// Stop proxying if either the server or client wants to disconnect
		var proxyCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
			context.RequestAborted,
			applicationStoppingToken).Token;

		// We allow for the case where the target isn't known ahead of time, and want to
		// delay proxied requests until the target becomes known. This is useful, for example,
		// when proxying to Angular CLI middleware: we won't know what port it's listening
		// on until it finishes starting up.
		var baseUri = await baseUriTask;
		var baseUriAsString = baseUri.ToString();
		var targetUri = new Uri((baseUriAsString.EndsWith("/", StringComparison.OrdinalIgnoreCase) ? baseUriAsString[..^1] : baseUriAsString)
			+ context.Request.Path
			+ context.Request.QueryString);

		try
		{
			if (context.WebSockets.IsWebSocketRequest)
			{
				await AcceptProxyWebSocketRequest(context, ToWebSocketScheme(targetUri), proxyCancellationToken);
				return true;
			}
			else
			{
				using (var requestMessage = CreateProxyHttpRequest(context, targetUri))
				using (var responseMessage = await httpClient.SendAsync(
					requestMessage,
					HttpCompletionOption.ResponseHeadersRead,
					proxyCancellationToken))
				{
					if (!proxy404s)
					{
						if (responseMessage.StatusCode == HttpStatusCode.NotFound)
						{
							// We're not proxying 404s, i.e., we want to resume the middleware pipeline
							// and let some other middleware handle this.
							return false;
						}
					}

					await CopyProxyHttpResponse(context, responseMessage, proxyCancellationToken);
					return true;
				}
			}
		}
		catch (OperationCanceledException)
		{
			// If we're aborting because either the client disconnected, or the server
			// is shutting down, don't treat this as an error.
			return true;
		}
		catch (IOException)
		{
			// This kind of exception can also occur if a proxy read/write gets interrupted
			// due to the process shutting down.
			return true;
		}
		catch (HttpRequestException ex)
		{
			throw new HttpRequestException(
				$"Failed to proxy the request to {targetUri.ToString()}, because the request to " +
				$"the proxy target failed. Check that the proxy target server is running and " +
				$"accepting requests to {baseUri.ToString()}.\n\n" +
				$"The underlying exception message was '{ex.Message}'." +
				$"Check the InnerException for more details.", ex);
		}
	}

	private static HttpRequestMessage CreateProxyHttpRequest(HttpContext context, Uri uri)
	{
		var request = context.Request;

		var requestMessage = new HttpRequestMessage();
		var requestMethod = request.Method;
		if (!HttpMethods.IsGet(requestMethod) &&
			!HttpMethods.IsHead(requestMethod) &&
			!HttpMethods.IsDelete(requestMethod) &&
			!HttpMethods.IsTrace(requestMethod))
		{
			var streamContent = new StreamContent(request.Body);
			requestMessage.Content = streamContent;
		}

		// Copy the request headers
		foreach (var header in request.Headers)
		{
			if (NotForwardedHttpHeaders.Contains(header.Key))
			{
				continue;
			}

			if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && requestMessage.Content != null)
			{
				requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
			}
		}

		requestMessage.Headers.Host = uri.Authority;
		requestMessage.RequestUri = uri;
		requestMessage.Method = new HttpMethod(request.Method);

		return requestMessage;
	}

	private static async Task CopyProxyHttpResponse(HttpContext context, HttpResponseMessage responseMessage, CancellationToken cancellationToken)
	{
		context.Response.StatusCode = (int)responseMessage.StatusCode;
		foreach (var header in responseMessage.Headers)
		{
			if ((HttpProtocol.IsHttp2(context.Request.Protocol) || HttpProtocol.IsHttp3(context.Request.Protocol))
				&& InvalidH2H3Headers.Contains(header.Key))
			{
				continue;
			}
			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		foreach (var header in responseMessage.Content.Headers)
		{
			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		// SendAsync removes chunking from the response. This removes the header so it doesn't expect a chunked response.
		context.Response.Headers.Remove("transfer-encoding");

		using (var responseStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken))
		{
			await responseStream.CopyToAsync(context.Response.Body, StreamCopyBufferSize, cancellationToken);
		}
	}

	private static Uri ToWebSocketScheme(Uri uri)
	{
		ArgumentNullException.ThrowIfNull(uri);

		var uriBuilder = new UriBuilder(uri);
		if (string.Equals(uriBuilder.Scheme, "https", StringComparison.OrdinalIgnoreCase))
		{
			uriBuilder.Scheme = "wss";
		}
		else if (string.Equals(uriBuilder.Scheme, "http", StringComparison.OrdinalIgnoreCase))
		{
			uriBuilder.Scheme = "ws";
		}

		return uriBuilder.Uri;
	}

	private static async Task<bool> AcceptProxyWebSocketRequest(HttpContext context, Uri destinationUri, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(destinationUri);

		using (var client = new ClientWebSocket())
		{
			foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
			{
				client.Options.AddSubProtocol(protocol);
			}
			foreach (var headerEntry in context.Request.Headers)
			{
				if (!NotForwardedWebSocketHeaders.Contains(headerEntry.Key))
				{
					try
					{
						client.Options.SetRequestHeader(headerEntry.Key, headerEntry.Value);
					}
					catch (ArgumentException)
					{
						// On net462, certain header names are reserved and can't be set.
						// We filter out the known ones via the test above, but there could
						// be others arbitrarily set by the client. It's not helpful to
						// consider it an error, so just skip non-forwardable headers.
						// The perf implications of handling this via a catch aren't an
						// issue since this is a dev-time only feature.
					}
				}
			}

			try
			{
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
				await client.ConnectAsync(destinationUri, cancellationToken);
			}
			catch (WebSocketException)
			{
				context.Response.StatusCode = 400;
				return false;
			}

			using (var server = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol))
			{
				var bufferSize = DefaultWebSocketBufferSize;
				await Task.WhenAll(
					PumpWebSocket(client, server, bufferSize, cancellationToken),
					PumpWebSocket(server, client, bufferSize, cancellationToken));
			}

			return true;
		}
	}

	private static async Task PumpWebSocket(WebSocket source, WebSocket destination, int bufferSize, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

		var buffer = new byte[bufferSize];

		while (true)
		{
			// Because WebSocket.ReceiveAsync doesn't work well with CancellationToken (it doesn't
			// actually exit when the token notifies, at least not in the 'server' case), use
			// polling. The perf might not be ideal, but this is a dev-time feature only.
			var resultTask = source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
			while (true)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}

				if (resultTask.IsCompleted)
				{
					break;
				}

				await Task.Delay(100, cancellationToken);
			}

			var result = resultTask.Result; // We know it's completed already
			if (result.MessageType == WebSocketMessageType.Close)
			{
				if (destination.State == WebSocketState.Open || destination.State == WebSocketState.CloseReceived)
				{
					await destination.CloseOutputAsync(source.CloseStatus!.Value, source.CloseStatusDescription, cancellationToken);
				}

				return;
			}

			await destination.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
		}
	}
}
