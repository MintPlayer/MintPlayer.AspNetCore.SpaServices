using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Npm;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Npm;

/// <summary>
/// Progress output: stderr text that arrives without a newline is echoed straight to the console.
/// These redirect <see cref="Console.Out"/>, which is process-wide, hence the collection.
/// </summary>
/// <remarks>
/// Each test writes a fresh token and asserts only on it, so a stray write from elsewhere in the
/// run cannot make it pass or fail.
/// </remarks>
[Collection(ProcessGlobalStateCollection.Name)]
public class NodeScriptRunnerConsoleTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task Writes_newline_free_stderr_chunks_to_the_console()
	{
		var token = Guid.NewGuid().ToString("n");

		var console = await RunWithConsole(["50% building " + token], "50% building");

		Assert.Contains("50% building " + token, console);
	}

	[Fact]
	public async Task Writes_a_line_split_across_two_reads_to_the_console_once()
	{
		// The first read is echoed as progress output. When the rest of the line arrives, the whole
		// line used to be logged as well - and with a console logger the echoed part appeared twice.
		var token = Guid.NewGuid().ToString("n");

		var console = await RunWithConsole(["compiling " + token, " done\n"], "done");

		Assert.Equal(1, Regex.Matches(console, token).Count);
		Assert.Contains("compiling " + token + " done", console);
	}

	/// <summary>
	/// Runs a runner whose stderr delivers <paramref name="stdErrReads"/> one read at a time, with a
	/// logger that writes to the console the way the console logger does, and returns everything
	/// that reached the console once a stderr line matching <paramref name="lastLine"/> was handled.
	/// </summary>
	private static async Task<string> RunWithConsole(string[] stdErrReads, string lastLine)
	{
		var original = Console.Out;
		var captured = new StringWriter();
		Console.SetOut(TextWriter.Synchronized(captured));
		try
		{
			var launcher = new NodeScriptRunnerTests.FakeLauncher(new NodeScriptRunnerTests.FakeChildProcess([""], stdErrReads));
			var runner = new NodeScriptRunner("C:/app", "build", null, null, "npm", new DiagnosticListener("test"), CancellationToken.None, launcher);

			runner.AttachToLogger(new ConsoleWritingLogger());
			launcher.Child.Release();

			// Subscribed after AttachToLogger, so by the time this resolves the runner's own stderr
			// handlers have already run for that line.
			await runner.StdErr.WaitForMatch(new Regex(Regex.Escape(lastLine))).WaitAsync(Deadline);

			return captured.ToString();
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	/// <summary>Writes every entry to <see cref="Console.Out"/>, standing in for the console logger.</summary>
	private sealed class ConsoleWritingLogger : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			=> Console.Out.WriteLine($"{logLevel}: {formatter(state, exception)}");
	}
}
