using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.SpaServices.Npm;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Npm;

/// <summary>
/// The script runner driven through a fake launcher. No package manager is installed, no process is
/// started, and nothing here touches the filesystem.
/// </summary>
public class NodeScriptRunnerTests
{
	private static NodeScriptRunner Create(FakeLauncher launcher, DiagnosticSource? diagnosticSource = null, CancellationToken stoppingToken = default)
		=> new(
			"C:/app",
			"build",
			null,
			null,
			"npm",
			diagnosticSource ?? new DiagnosticListener("test"),
			stoppingToken,
			launcher);

	[Fact]
	public void Exposes_the_child_process_streams()
	{
		var launcher = new FakeLauncher("out", "err");
		launcher.Child.Release();

		using var runner = (IDisposable)Create(launcher);

		Assert.NotNull(((NodeScriptRunner)runner).StdOut);
		Assert.NotNull(((NodeScriptRunner)runner).StdErr);
	}

	[Fact]
	public void Passes_the_composed_start_info_to_the_launcher()
	{
		var launcher = new FakeLauncher();

		using var runner = (IDisposable)Create(launcher);

		Assert.NotNull(launcher.LastStartInfo);
		Assert.Equal("C:/app", launcher.LastStartInfo!.WorkingDirectory);
		Assert.Contains("run build", launcher.LastStartInfo.Arguments);
	}

	[Fact]
	public void Wraps_a_launch_failure_with_a_PATH_hint()
	{
		// This is the message a developer actually sees when npm is not installed, so it is worth
		// pinning: it names the command and tells them where to look.
		var launcher = new FakeLauncher { ThrowOnStart = new InvalidOperationException("no such file") };

		var ex = Assert.Throws<InvalidOperationException>(() => Create(launcher));

		Assert.Contains("Failed to start 'npm'", ex.Message);
		Assert.Contains("PATH", ex.Message);
		Assert.NotNull(ex.InnerException);
	}

	[Fact]
	public void Kills_the_whole_process_tree_on_dispose()
	{
		var launcher = new FakeLauncher();
		var runner = Create(launcher);

		((IDisposable)runner).Dispose();

		// entireProcessTree matters: npm spawns the real build as a grandchild, and killing only npm
		// leaves it running and holding the port.
		Assert.True(launcher.Child.Killed);
		Assert.True(launcher.Child.KilledEntireTree);
	}

	[Fact]
	public void Does_not_kill_a_process_that_already_exited()
	{
		var launcher = new FakeLauncher();
		launcher.Child.HasExited = true;
		var runner = Create(launcher);

		((IDisposable)runner).Dispose();

		Assert.False(launcher.Child.Killed);
	}

	[Fact]
	public void Disposes_when_the_application_stopping_token_fires()
	{
		var launcher = new FakeLauncher();
		using var cts = new CancellationTokenSource();
		_ = Create(launcher, stoppingToken: cts.Token);

		cts.Cancel();

		// The runner registers its own disposal on the token so a host shutdown takes the child with it.
		Assert.True(launcher.Child.Killed);
	}

	[Fact]
	public async Task Forwards_stdout_lines_to_the_logger_with_colours_stripped()
	{
		// The reader starts consuming inside the runner's constructor, so the streams are gated: no
		// byte flows until AttachToLogger has subscribed. Without that the line can be emitted and
		// dropped before the handler exists, which made this test flaky roughly one run in three.
		var launcher = new FakeLauncher("\u001b[32mcompiled successfully\u001b[0m\n");
		var logger = new CollectingLogger();
		var runner = Create(launcher);

		runner.AttachToLogger(logger);
		launcher.Child.Release();

		var line = await logger.WaitForInformation(l => l.Contains("compiled successfully"));

		// A logger is not a terminal; forwarding escape codes makes the log unreadable.
		Assert.DoesNotContain('\u001b', line);
	}

	[Fact]
	public async Task Forwards_stderr_lines_as_errors()
	{
		var launcher = new FakeLauncher(stdOut: "", stdErr: "ERROR in ./src/app.ts\n");
		var logger = new CollectingLogger();
		var runner = Create(launcher);

		runner.AttachToLogger(logger);
		launcher.Child.Release();

		await logger.WaitForError(l => l.Contains("ERROR in ./src/app.ts"));
	}

	[Fact]
	public async Task Ignores_blank_lines()
	{
		// A blank line followed by a real one: waiting for the real line proves the reader got past
		// the blanks, so asserting their absence is deterministic rather than a race against nothing.
		var launcher = new FakeLauncher("\n   \nreal line\n");
		var logger = new CollectingLogger();
		var runner = Create(launcher);

		runner.AttachToLogger(logger);
		launcher.Child.Release();

		await logger.WaitForInformation(l => l.Contains("real line"));

		Assert.DoesNotContain(logger.Information, l => string.IsNullOrWhiteSpace(l));
	}

	[Fact]
	public void Emits_a_diagnostic_event_when_a_listener_is_subscribed()
	{
		var listener = new DiagnosticListener("test");
		var observer = new RecordingObserver();
		using var subscription = listener.Subscribe(observer);

		using var runner = (IDisposable)Create(new FakeLauncher(), listener);

		Assert.Contains("MintPlayer.AspNetCore.NodeServices.Npm.NpmStarted", observer.Events);
	}

	[Theory]
	[InlineData("\u001b[32mgreen\u001b[0m", "green")]
	[InlineData("\u001b[1;31mbold red\u001b[0m", "bold red")]
	[InlineData("no colours here", "no colours here")]
	[InlineData("", "")]
	public void StripAnsiColors_removes_escape_sequences(string input, string expected)
	{
		Assert.Equal(expected, NodeScriptRunner.StripAnsiColors(input));
	}

	private sealed class FakeLauncher(string stdOut = "", string stdErr = "") : IProcessLauncher
	{
		public FakeChildProcess Child { get; } = new(stdOut, stdErr);

		public ProcessStartInfo? LastStartInfo { get; private set; }

		public Exception? ThrowOnStart { get; init; }

		public IChildProcess Start(ProcessStartInfo startInfo)
		{
			LastStartInfo = startInfo;
			if (ThrowOnStart is not null)
				throw ThrowOnStart;

			return Child;
		}
	}

	private sealed class FakeChildProcess : IChildProcess
	{
		private readonly GatedStream outStream;
		private readonly GatedStream errStream;

		public FakeChildProcess(string stdOut, string stdErr)
		{
			outStream = new GatedStream(stdOut);
			errStream = new GatedStream(stdErr);
			StandardOutput = new StreamReader(outStream);
			StandardError = new StreamReader(errStream);
		}

		public StreamReader StandardOutput { get; }

		public StreamReader StandardError { get; }

		public bool HasExited { get; set; }

		public bool Killed { get; private set; }

		public bool KilledEntireTree { get; private set; }

		/// <summary>Lets the readers start, once the test has attached its handlers.</summary>
		public void Release()
		{
			outStream.Release();
			errStream.Release();
		}

		public void Kill(bool entireProcessTree)
		{
			Killed = true;
			KilledEntireTree = entireProcessTree;
		}

		public void Dispose() { }
	}

	private sealed class CollectingLogger : ILogger
	{
		private readonly Lock gate = new();
		private readonly List<string> information = [];
		private readonly List<string> errors = [];

		public IReadOnlyList<string> Information
		{
			get { lock (gate) return [.. information]; }
		}

		public IReadOnlyList<string> Errors
		{
			get { lock (gate) return [.. errors]; }
		}

		/// <summary>
		/// Waits for a matching line rather than sleeping for a fixed period. The reader delivers on a
		/// background task, so the only deterministic synchronisation is the arrival of the line
		/// itself; the timeout turns a hang into a readable failure.
		/// </summary>
		public Task<string> WaitForInformation(Func<string, bool> predicate) => WaitFor(() => Information, predicate);

		public Task<string> WaitForError(Func<string, bool> predicate) => WaitFor(() => Errors, predicate);

		private static async Task<string> WaitFor(Func<IReadOnlyList<string>> snapshot, Func<string, bool> predicate)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			while (true)
			{
				var match = snapshot().FirstOrDefault(predicate);
				if (match is not null)
					return match;

				if (timeout.IsCancellationRequested)
					throw new TimeoutException($"No matching line arrived. Saw: [{string.Join(" | ", snapshot())}]");

				await Task.Yield();
			}
		}

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			var message = formatter(state, exception);
			lock (gate)
			{
				if (logLevel == LogLevel.Error)
					errors.Add(message);
				else
					information.Add(message);
			}
		}

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();

			public void Dispose() { }
		}
	}

	private sealed class RecordingObserver : IObserver<KeyValuePair<string, object?>>
	{
		public List<string> Events { get; } = [];

		public void OnCompleted() { }

		public void OnError(Exception error) { }

		public void OnNext(KeyValuePair<string, object?> value) => Events.Add(value.Key);
	}
}
