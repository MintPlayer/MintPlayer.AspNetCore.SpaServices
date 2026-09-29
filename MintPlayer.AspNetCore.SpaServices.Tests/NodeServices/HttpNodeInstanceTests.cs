using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// <see cref="HttpNodeInstance"/> end to end, with a <see cref="FakeNodeProcess"/> in place of node and a
/// recording <see cref="HttpMessageHandler"/> in place of its HTTP listener.
/// </summary>
public class HttpNodeInstanceTests
{
	private const string PortLine = "[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {127.0.0.1} port 5123]";

	private readonly FakeNodeProcess process = new();
	private readonly CapturingNodeLogger logger = new();
	private readonly RecordingHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.OK)
	{
		Content = new StringContent("\"done\"", Encoding.UTF8, "application/json"),
	});

	[Fact]
	public async Task Invoke_posts_camelCase_payload_to_parsed_endpoint()
	{
		using var instance = NewInstance();
		process.EmitStdout(PortLine);
		process.EmitListening();

		var result = await instance.InvokeExportAsync<string>(
			CancellationToken.None, "module.js", "render", new { SomeValue = 1 });

		Assert.Equal("done", result);
		Assert.Equal(HttpMethod.Post, handler.LastMethod);
		Assert.Equal(new Uri("http://127.0.0.1:5123/"), handler.LastUri);
		Assert.Equal(
			"{\"moduleName\":\"module.js\",\"exportedFunctionName\":\"render\",\"args\":[{\"someValue\":1}]}",
			handler.LastBody);
		// The port line is consumed, not logged.
		Assert.Empty(logger.Entries);
	}

	[Fact]
	public void Launches_node_with_the_http_entry_point_and_the_port()
	{
		using var instance = NewInstance(port: 4000);

		Assert.Equal("custom-node", process.StartInfo!.FileName);
		Assert.EndsWith("--port 4000", process.StartInfo.Arguments);
		Assert.Contains("HttpNodeHost:Listening on", File.ReadAllText(OutOfProcessNodeInstanceTests.EntryPointOf(process)));
	}

	[Fact]
	public void Second_port_line_is_logged()
	{
		using var instance = NewInstance();

		process.EmitStdout(PortLine);
		process.EmitStdout("[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {::1} port 6000]");

		var entry = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Information, entry.Level);
		Assert.Contains("port 6000", entry.Message);
	}

	[Fact]
	public void Dispose_disposes_client_once()
	{
		var instance = NewInstance();

		instance.Dispose();
		instance.Dispose();

		Assert.Equal(1, handler.DisposeCount);
		Assert.True(process.Disposed);
	}

	private HttpNodeInstance NewInstance(int port = 0)
	{
		var options = new NodeServicesOptions(new ServiceCollection().BuildServiceProvider())
		{
			NodeInstanceOutputLogger = logger,
			NodePath = "custom-node",
			ProjectPath = Path.GetTempPath(),
			WatchFileExtensions = [],
			InvocationTimeoutMilliseconds = 10 * 1000,
		};
		return new HttpNodeInstance(options, port, process.Start, handler);
	}

	private sealed class RecordingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
	{
		public HttpMethod? LastMethod { get; private set; }

		public Uri? LastUri { get; private set; }

		public string? LastBody { get; private set; }

		public int DisposeCount { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastMethod = request.Method;
			LastUri = request.RequestUri;
			LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			return respond();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				DisposeCount++;
			}

			base.Dispose(disposing);
		}
	}
}
