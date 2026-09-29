// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MintPlayer.AspNetCore.NodeServices.HostingModels;

/// <summary>
/// A specialisation of the OutOfProcessNodeInstance base class that uses HTTP to perform RPC invocations.
///
/// The Node child process starts an HTTP listener on an arbitrary available port (except where a nonzero
/// port number is specified as a constructor parameter), and signals which port was selected using the same
/// input/output-based mechanism that the base class uses to determine when the child process is ready to
/// accept RPC invocations.
/// </summary>
/// <seealso cref="MintPlayer.AspNetCore.NodeServices.HostingModels.OutOfProcessNodeInstance" />
internal class HttpNodeInstance : OutOfProcessNodeInstance
{
	private static readonly Regex EndpointMessageRegex =
		new Regex(@"^\[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {(.*?)} port (\d+)\]$");

	private static readonly JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
	{
		ContractResolver = new CamelCasePropertyNamesContractResolver(),
		TypeNameHandling = TypeNameHandling.None
	};

	private readonly HttpClient _client;
	private bool _disposed;
	private string? _endpoint;

	public HttpNodeInstance(NodeServicesOptions options, int port = 0)
	: base(
			EmbeddedResourceReader.Read(typeof(HttpNodeInstance), "/Content/Node/entrypoint-http.js"),
			options.ProjectPath,
			options.WatchFileExtensions,
			MakeCommandLineOptions(port),
			options.ApplicationStoppingToken,
			options.NodeInstanceOutputLogger,
			options.EnvironmentVariables,
			options.InvocationTimeoutMilliseconds,
			options.LaunchWithDebugging,
			options.DebuggingPort,
			options.NodePath)
	{
		_client = new HttpClient();
		_client.Timeout = TimeSpan.FromMilliseconds(options.InvocationTimeoutMilliseconds + 1000);
	}

	private static string MakeCommandLineOptions(int port)
	{
		return $"--port {port}";
	}

	protected override async Task<T> InvokeExportAsync<T>(
		NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
	{
		var payloadJson = JsonConvert.SerializeObject(invocationInfo, jsonSerializerSettings);
		var payload = new StringContent(payloadJson, Encoding.UTF8, "application/json");
		var response = await _client.PostAsync(_endpoint, payload, cancellationToken);

		return await ReadResponseAsync<T>(response, cancellationToken);
	}

	/// <summary>
	/// Turns the Node entry point's HTTP response into the invocation result, or into the exception it describes.
	/// <para>
	/// Internal so the content-type contract with entrypoint-http.js can be tested without node. The response is
	/// disposed here, except when its body is handed to the caller as a <see cref="Stream"/>.
	/// </para>
	/// </summary>
	internal static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var responseIsOwnedByCaller = false;
		try
		{
			if (!response.IsSuccessStatusCode)
			{
				// Unfortunately there's no true way to cancel ReadAsStringAsync calls, hence AbandonIfCancelled
				var responseBody = await response.Content.ReadAsStringAsync().OrThrowOnCancellation(cancellationToken);
				throw CreateErrorResponseException(response, responseBody);
			}

			var mediaType = response.Content.Headers.ContentType?.MediaType;
			if (mediaType == null)
			{
				throw new InvalidOperationException(
					"Node responded without a Content-Type, so the response cannot be converted to " + typeof(T).FullName + ".");
			}

			switch (mediaType)
			{
				case "text/plain":
					// String responses can skip JSON encoding/decoding
					if (typeof(T) != typeof(string))
					{
						throw new ArgumentException(
							"Node module responded with non-JSON string. This cannot be converted to the requested generic type: " +
							typeof(T).FullName);
					}

					var responseString = await response.Content.ReadAsStringAsync().OrThrowOnCancellation(cancellationToken);
					return (T)(object)responseString;

				case "application/json":
					var responseJson = await response.Content.ReadAsStringAsync().OrThrowOnCancellation(cancellationToken);
					return JsonConvert.DeserializeObject<T>(responseJson, jsonSerializerSettings)!;

				case "application/octet-stream":
					// Streamed responses have to be received as System.IO.Stream instances
					if (typeof(T) != typeof(Stream) && typeof(T) != typeof(object))
					{
						throw new ArgumentException(
							"Node module responded with binary stream. This cannot be converted to the requested generic type: " +
							typeof(T).FullName + ". Instead you must use the generic type System.IO.Stream.");
					}

					var responseStream = await response.Content.ReadAsStreamAsync().OrThrowOnCancellation(cancellationToken);
					responseIsOwnedByCaller = true;
					return (T)(object)responseStream;

				default:
					throw new InvalidOperationException("Unexpected response content type: " + mediaType);
			}
		}
		finally
		{
			if (!responseIsOwnedByCaller)
			{
				response.Dispose();
			}
		}
	}

	private static NodeInvocationException CreateErrorResponseException(HttpResponseMessage response, string responseBody)
	{
		RpcJsonResponse? responseError = null;
		try
		{
			responseError = JsonConvert.DeserializeObject<RpcJsonResponse>(responseBody, jsonSerializerSettings);
		}
		catch (JsonException)
		{
			// Not the error body the Node entry point writes - e.g. an HTML error page from something in between.
		}

		if (responseError?.ErrorMessage != null)
		{
			return new NodeInvocationException(responseError.ErrorMessage, responseError.ErrorDetails);
		}

		return new NodeInvocationException(
			$"Node responded with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) and no error description.",
			responseBody);
	}

	/// <summary>
	/// Reads the endpoint out of the entry point's "Listening on {address} port N" line, or returns null for any other line.
	/// </summary>
	/// <remarks>Internal so the stdout contract with entrypoint-http.js can be tested without node.</remarks>
	internal static string? ParseEndpoint(string line)
	{
		var match = EndpointMessageRegex.Match(line);
		if (!match.Success)
		{
			return null;
		}

		var port = int.Parse(match.Groups[2].Captures[0].Value);
		var resolvedIpAddress = match.Groups[1].Captures[0].Value;

		// IPv6 must be wrapped with [] brackets. Node reports every address without them, not only ::1.
		if (resolvedIpAddress.Contains(':'))
		{
			resolvedIpAddress = $"[{resolvedIpAddress}]";
		}

		return $"http://{resolvedIpAddress}:{port}";
	}

	protected override void OnOutputDataReceived(string outputData)
	{
		// Watch for "port selected" messages, and when observed,
		// store the IP (IPv4/IPv6) and port number
		// so we can use it when making HTTP requests. The child process will always send
		// one of these messages before it sends a "ready for connections" message.
		var endpoint = string.IsNullOrEmpty(_endpoint) ? ParseEndpoint(outputData) : null;
		if (endpoint != null)
		{
			_endpoint = endpoint;
		}
		else
		{
			base.OnOutputDataReceived(outputData);
		}
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		if (!_disposed)
		{
			if (disposing)
			{
				_client.Dispose();
			}

			_disposed = true;
		}
	}

#pragma warning disable 649 // These properties are populated via JSON deserialization
	private class RpcJsonResponse
	{
		public string ErrorMessage { get; set; }
		public string ErrorDetails { get; set; }
	}
#pragma warning restore 649
}
