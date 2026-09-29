using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The C# half of the contract with entrypoint-http.js: the "Listening on" stdout line, and how each
/// HTTP response the entry point can write becomes a result or an exception.
/// </summary>
public class HttpNodeInstanceProtocolTests
{
	#region ParseEndpoint

	[Theory]
	[InlineData("127.0.0.1", "51234", "http://127.0.0.1:51234")]
	[InlineData("::1", "8080", "http://[::1]:8080")]
	// B5: only ::1 used to get brackets, so any other IPv6 address produced an unparseable URI.
	[InlineData("::", "8080", "http://[::]:8080")]
	[InlineData("fe80::1", "9000", "http://[fe80::1]:9000")]
	public void ParseEndpoint_builds_a_valid_uri_for_every_address_family(string address, string port, string expected)
	{
		var endpoint = HttpNodeInstance.ParseEndpoint(
			$"[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {{{address}}} port {port}]");

		Assert.Equal(expected, endpoint);
		Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out _), $"'{endpoint}' is not a valid URI.");
	}

	[Theory]
	[InlineData("[MintPlayer.AspNetCore.NodeServices:Listening]")]
	[InlineData("Some ordinary log output")]
	[InlineData("")]
	public void ParseEndpoint_returns_null_for_any_other_line(string line)
		=> Assert.Null(HttpNodeInstance.ParseEndpoint(line));

	#endregion

	#region ReadResponseAsync

	[Fact]
	public async Task Plain_text_becomes_a_string()
	{
		var content = new TrackingContent("hello", "text/plain");

		var result = await HttpNodeInstance.ReadResponseAsync<string>(Ok(content), CancellationToken.None);

		Assert.Equal("hello", result);
		Assert.True(content.Disposed, "A fully read response was not disposed.");
	}

	[Fact]
	public async Task Plain_text_cannot_become_anything_but_a_string()
	{
		var content = new TrackingContent("hello", "text/plain");

		var ex = await Assert.ThrowsAsync<ArgumentException>(
			() => HttpNodeInstance.ReadResponseAsync<int>(Ok(content), CancellationToken.None));

		Assert.Contains("non-JSON string", ex.Message);
		Assert.True(content.Disposed);
	}

	[Fact]
	public async Task Json_is_deserialized_with_camel_case_names()
	{
		var content = new TrackingContent("{\"name\":\"node\",\"count\":3}", "application/json");

		var result = await HttpNodeInstance.ReadResponseAsync<Payload>(Ok(content), CancellationToken.None);

		Assert.Equal("node", result.Name);
		Assert.Equal(3, result.Count);
		Assert.True(content.Disposed);
	}

	[Fact]
	public async Task A_binary_stream_is_handed_to_the_caller_undisposed()
	{
		var content = new TrackingContent("bytes", "application/octet-stream");

		using var stream = await HttpNodeInstance.ReadResponseAsync<Stream>(Ok(content), CancellationToken.None);

		Assert.False(content.Disposed, "The caller's stream was disposed out from under it.");
		using var reader = new StreamReader(stream);
		Assert.Equal("bytes", await reader.ReadToEndAsync());
	}

	[Fact]
	public async Task A_binary_stream_may_be_requested_as_object()
	{
		var content = new TrackingContent("bytes", "application/octet-stream");

		var result = await HttpNodeInstance.ReadResponseAsync<object>(Ok(content), CancellationToken.None);

		Assert.IsAssignableFrom<Stream>(result);
		((Stream)result).Dispose();
	}

	[Fact]
	public async Task A_binary_stream_cannot_become_a_string()
	{
		var content = new TrackingContent("bytes", "application/octet-stream");

		var ex = await Assert.ThrowsAsync<ArgumentException>(
			() => HttpNodeInstance.ReadResponseAsync<string>(Ok(content), CancellationToken.None));

		Assert.Contains("System.IO.Stream", ex.Message);
		Assert.True(content.Disposed);
	}

	[Fact]
	public async Task An_unknown_media_type_is_rejected()
	{
		var content = new TrackingContent("<p/>", "text/html");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => HttpNodeInstance.ReadResponseAsync<string>(Ok(content), CancellationToken.None));

		Assert.Equal("Unexpected response content type: text/html", ex.Message);
		Assert.True(content.Disposed);
	}

	[Fact]
	public async Task A_missing_content_type_is_rejected_with_a_clear_error()
	{
		// B3: this used to be a NullReferenceException on ContentType.MediaType.
		var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
		Assert.Null(content.Headers.ContentType);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => HttpNodeInstance.ReadResponseAsync<string>(Ok(content), CancellationToken.None));

		Assert.Contains("without a Content-Type", ex.Message);
	}

	[Fact]
	public async Task A_json_error_body_becomes_a_NodeInvocationException()
	{
		var content = new TrackingContent("{\"errorMessage\":\"boom\",\"errorDetails\":\"at line 1\"}", "application/json");

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => HttpNodeInstance.ReadResponseAsync<string>(Error(HttpStatusCode.InternalServerError, content), CancellationToken.None));

		Assert.StartsWith("boom", ex.Message);
		Assert.Contains("at line 1", ex.Message);
		Assert.False(ex.NodeInstanceUnavailable);
		Assert.True(content.Disposed);
	}

	[Theory]
	// B4: an empty body used to deserialize to null and throw NullReferenceException...
	[InlineData("")]
	// ...a non-JSON body (an HTML 502 from something in between) threw JsonReaderException...
	[InlineData("<html><body>Bad Gateway</body></html>")]
	// ...and JSON of another shape produced an exception with a null message.
	[InlineData("{\"unrelated\":true}")]
	[InlineData("\"just a string\"")]
	public async Task An_error_body_that_is_not_the_entry_points_json_still_becomes_a_NodeInvocationException(string body)
	{
		var content = new TrackingContent(body, "text/html");

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => HttpNodeInstance.ReadResponseAsync<string>(Error(HttpStatusCode.BadGateway, content), CancellationToken.None));

		Assert.Contains("HTTP 502", ex.Message);
		Assert.Contains(body, ex.Message);
		Assert.True(content.Disposed);
	}

	#endregion

	private static HttpResponseMessage Ok(HttpContent content)
		=> new(HttpStatusCode.OK) { Content = content };

	private static HttpResponseMessage Error(HttpStatusCode status, HttpContent content)
		=> new(status) { Content = content };

	private sealed class Payload
	{
		public string? Name { get; set; }

		public int Count { get; set; }
	}

	/// <summary>Records its own disposal, which is how a disposed <see cref="HttpResponseMessage"/> shows.</summary>
	private sealed class TrackingContent : StringContent
	{
		public TrackingContent(string content, string mediaType) : base(content, Encoding.UTF8)
		{
			Headers.ContentType = new MediaTypeHeaderValue(mediaType);
		}

		public bool Disposed { get; private set; }

		protected override void Dispose(bool disposing)
		{
			Disposed = true;
			base.Dispose(disposing);
		}
	}
}
