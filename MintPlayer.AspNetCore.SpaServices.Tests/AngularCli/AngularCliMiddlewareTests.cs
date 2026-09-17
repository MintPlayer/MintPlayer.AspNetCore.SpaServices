using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.SpaServices.AngularCli;
using MintPlayer.AspNetCore.SpaServices.Npm;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.AngularCli;

/// <summary>
/// The Angular CLI start-up handshake: launch the dev server, scrape its stdout for the URL it is
/// listening on, then wait for it to answer. Driven entirely through fakes - no Angular, no npm, no
/// socket.
/// </summary>
/// <remarks>
/// These could not be written until <c>EventedStreamReader</c> kept a history of emitted lines. A
/// fake process delivers all of its output in a single read, so every sequential <c>WaitForMatch</c>
/// lost the race against the reader and hung - see <c>WaitForMatchSequenceTests</c>.
/// <para>
/// The readiness poll is still an unbounded <c>while (true)</c> with no cancellation, so every test
/// here supplies a stub handler that answers on the first attempt.
/// </para>
/// </remarks>
public class AngularCliMiddlewareTests
{
	private static readonly Regex DefaultRegex = new("open your browser on (?<openbrowser>http\\S+)");

	private static Task<Uri> Start(string stdOut, string stdErr = "", Regex[]? regexes = null, HttpMessageHandler? readiness = null)
		=> AngularCliMiddleware.StartAngularCliServerAsync(
			"C:/app",
			"start",
			"npm",
			4200,
			regexes ?? [DefaultRegex],
			NullLogger.Instance,
			new DiagnosticListener("test"),
			CancellationToken.None,
			new ScriptedLauncher(stdOut, stdErr),
			readiness ?? new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

	[Fact]
	public async Task Returns_the_url_the_dev_server_announces()
	{
		var uri = await Start("** Angular Live Development Server is listening, open your browser on http://localhost:4200/ **\n");

		Assert.Equal(new Uri("http://localhost:4200/"), uri);
	}

	[Fact]
	public async Task Treats_any_http_response_as_ready_even_a_404()
	{
		// A 404 still proves something is listening, which is all the poll is checking for.
		using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));

		var uri = await Start("open your browser on http://localhost:4200/\n", readiness: handler);

		Assert.Equal(new Uri("http://localhost:4200/"), uri);
		Assert.Equal(1, handler.RequestCount);
		Assert.Equal(HttpMethod.Head, handler.LastRequest!.Method);
	}

	[Fact]
	public async Task Waits_for_every_supplied_regex_in_turn()
	{
		// Several regexes mean several build phases, and the URL may only appear in the last. Both
		// lines arrive in one read here, which is precisely what used to hang.
		var uri = await Start(
			"Build at: step one\nopen your browser on http://localhost:4200/\n",
			regexes: [new Regex(@"Build at: (?<step>\w+)"), DefaultRegex]);

		Assert.Equal(new Uri("http://localhost:4200/"), uri);
	}

	[Fact]
	public async Task Fails_when_a_custom_regex_has_no_openbrowser_group()
	{
		// Without that group the middleware has no way to learn the port, so this has to be an error
		// rather than a silent hang.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Start("compiled successfully\n", regexes: [new Regex(@"compiled (?<other>\w+)")]));

		Assert.Contains("openbrowser", ex.Message);
	}

	[Fact]
	public async Task Reports_the_stderr_output_when_the_script_exits_early()
	{
		// The dev server died before announcing itself. The developer needs the script's own stderr,
		// not a bare "stream ended" - and now that the reader keeps a history, it is actually there.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Start(stdOut: "", stdErr: "Error: Cannot find module '@angular/cli'\n"));

		Assert.Contains("exited without indicating", ex.Message);
		Assert.Contains("npm", ex.Message);
		Assert.Contains("start", ex.Message);
	}

	/// <summary>Replays a fixed stdout/stderr script as if it were a package-manager process.</summary>
	private sealed class ScriptedLauncher(string stdOut, string stdErr) : IProcessLauncher
	{
		public IChildProcess Start(ProcessStartInfo startInfo) => new ScriptedChild(stdOut, stdErr);

		private sealed class ScriptedChild(string stdOut, string stdErr) : IChildProcess
		{
			public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdOut)));

			public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdErr)));

			public bool HasExited => true;

			public void Kill(bool entireProcessTree) { }

			public void Dispose() { }
		}
	}
}
