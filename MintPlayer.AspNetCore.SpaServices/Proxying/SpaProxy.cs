// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Primitives;

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

	/// <summary>
	/// Builds the handler the proxy uses. Split out so a test can assert the two settings that matter
	/// - redirects and cookies must be the client's business, not ours - without reflecting over
	/// <see cref="HttpMessageInvoker"/>'s private fields to find it.
	/// </summary>
	internal static HttpClientHandler CreateProxyHandler()
		=> new()
		{
			AllowAutoRedirect = false,
			UseCookies = false,
		};

	public static HttpClient CreateHttpClientForProxy(TimeSpan requestTimeout)
		=> new(CreateProxyHandler())
		{
			Timeout = requestTimeout
		};

	public static async Task<bool> PerformProxyRequest(
		HttpContext context,
		HttpClient httpClient,
		Task<Uri> baseUriTask,
		CancellationToken applicationStoppingToken,
		bool proxy404s)
	{
		// Stop proxying if either the server or client wants to disconnect.
		// The source is disposed rather than discarded: keeping only its Token left a registration
		// on applicationStoppingToken - which lives as long as the process - for every proxied
		// request.
		using var proxyCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
			context.RequestAborted,
			applicationStoppingToken);
		var proxyCancellationToken = proxyCancellationTokenSource.Token;

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

			// A content header (Content-Type, Content-Language, ...) is rejected by the request
			// header collection and belongs on the content instead. Bodiless methods have no
			// content to put it on, so give them an empty one rather than dropping the header
			// silently - a GET carrying Content-Type used to lose it with no trace.
			if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
			{
				requestMessage.Content ??= new StreamContent(Stream.Null);
				requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
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

	/// <summary>Maps an http/https origin onto its ws/wss equivalent. Internal so it can be tested directly.</summary>
	internal static Uri ToWebSocketScheme(Uri uri)
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

	/// <summary>
	/// The request headers that must NOT be forwarded to the upstream socket: the handshake headers
	/// are regenerated by the outbound client, and forwarding the originals corrupts it.
	/// </summary>
	internal static IEnumerable<KeyValuePair<string, StringValues>> ForwardableWebSocketHeaders(IHeaderDictionary headers)
		=> headers.Where(h => !NotForwardedWebSocketHeaders.Contains(h.Key));

	internal static async Task<bool> AcceptProxyWebSocketRequest(HttpContext context, Uri destinationUri, CancellationToken cancellationToken, IWebSocketConnector? connector = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(destinationUri);

		connector ??= ClientWebSocketConnector.Instance;

		WebSocket client;
		try
		{
			client = await connector.ConnectAsync(
				destinationUri,
				context.WebSockets.WebSocketRequestedProtocols,
				ForwardableWebSocketHeaders(context.Request.Headers),
				cancellationToken);
		}
		catch (WebSocketException)
		{
			// The dev server is not up, or refused the upgrade. A 400 is the honest answer; anything
			// else would look like the proxy itself failed.
			context.Response.StatusCode = 400;
			return false;
		}

		using (client)
		using (var server = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol))
		{
			var bufferSize = DefaultWebSocketBufferSize;
			await Task.WhenAll(
				PumpWebSocket(client, server, bufferSize, cancellationToken),
				PumpWebSocket(server, client, bufferSize, cancellationToken));
		}

		return true;
	}

	/// <summary>
	/// Copies frames one way until the source closes or the token trips. Internal so it can be driven
	/// with a pair of fake <see cref="WebSocket"/>s - no real socket, and no dev server, is needed.
	/// </summary>
	internal static async Task PumpWebSocket(WebSocket source, WebSocket destination, int bufferSize, CancellationToken cancellationToken)
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
