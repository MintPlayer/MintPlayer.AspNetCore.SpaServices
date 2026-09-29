using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The handshake, logging, timeout and disposal logic of <see cref="OutOfProcessNodeInstance"/>, driven
/// through the internal <see cref="INodeProcess"/> seam with a <see cref="FakeNodeProcess"/>.
/// </summary>
public class OutOfProcessNodeInstanceTests
{
	internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

	private readonly FakeNodeProcess process = new();
	private readonly CapturingNodeLogger logger = new();

	[Fact]
	public async Task Invoke_waits_for_Listening_then_returns()
	{
		NodeInvocationInfo? seen = null;
		using var instance = new TestNodeInstance(process, logger, invoke: (info, _) =>
		{
			seen = info;
			return Task.FromResult<object>("ok");
		});

		var call = instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", "render", 1, "two");
		Assert.False(call.IsCompleted, "The invocation did not wait for node to be listening.");

		process.EmitListening();

		Assert.Equal("ok", await call.WaitAsync(Deadline));
		Assert.Equal("module.js", seen!.ModuleName);
		Assert.Equal("render", seen.ExportedFunctionName);
		Assert.Equal(new object[] { 1, "two" }, seen.Args);
	}

	[Fact]
	public void Stdout_and_stderr_are_logged_unencoded()
	{
		using var instance = new TestNodeInstance(process, logger);

		process.EmitListening();
		process.EmitStdout("first__ns_newline__second");
		process.EmitStderr("broken__ns_newline__at line 1");
		// End-of-stream markers, not lines.
		process.EmitStdout(null);
		process.EmitStderr(null);

		Assert.Equal(
			new[]
			{
				(LogLevel.Information, "first" + Environment.NewLine + "second"),
				(LogLevel.Error, "broken" + Environment.NewLine + "at line 1"),
			},
			logger.Entries.ToArray());
	}

	[Fact]
	public void A_second_Listening_line_is_ordinary_output()
	{
		using var instance = new TestNodeInstance(process, logger);

		process.EmitListening();
		process.EmitListening();

		var entry = Assert.Single(logger.Entries);
		Assert.Equal((LogLevel.Information, "[MintPlayer.AspNetCore.NodeServices:Listening]"), entry);
	}

	[Theory]
	[InlineData(true, LogLevel.Warning)]
	[InlineData(false, LogLevel.Error)]
	public void Debugger_noise_on_stderr_logs_as_warning_only_when_debugging(bool launchWithDebugging, LogLevel expected)
	{
		using var instance = new TestNodeInstance(process, logger, launchWithDebugging: launchWithDebugging);

		process.EmitStderr("Debugger listening on ws://127.0.0.1:9229/abc");

		var entry = Assert.Single(logger.Entries);
		Assert.Equal((expected, "Debugger listening on ws://127.0.0.1:9229/abc"), entry);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HasExited_throws_unavailable(bool launchWithDebugging)
	{
		using var instance = new TestNodeInstance(process, logger, launchWithDebugging: launchWithDebugging);
		process.EmitListening();
		process.Exit();

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!));

		Assert.StartsWith("The Node process has exited", ex.Message);
		Assert.True(ex.NodeInstanceUnavailable);
		// A debugging node holds the inspector port, so it must go away at once rather than drain.
		Assert.Equal(!launchWithDebugging, ex.AllowConnectionDraining);
	}

	[Fact]
	public async Task Never_listening_times_out_connecting()
	{
		using var instance = new TestNodeInstance(process, logger, invocationTimeoutMilliseconds: 50);

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!).WaitAsync(Deadline));

		Assert.StartsWith("Attempt to connect to Node timed out after 50ms.", ex.Message);
	}

	[Fact]
	public async Task Hung_invocation_times_out()
	{
		using var instance = new TestNodeInstance(process, logger, invocationTimeoutMilliseconds: 50, invoke: async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return "unreachable";
		});
		process.EmitListening();

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!).WaitAsync(Deadline));

		Assert.StartsWith("The Node invocation timed out after 50ms.", ex.Message);
		Assert.Contains(nameof(MintPlayer.AspNetCore.NodeServices.NodeServicesOptions), ex.Message);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Caller_cancel_rethrows_TaskCanceled(bool listening)
	{
		using var instance = new TestNodeInstance(process, logger, invoke: async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return "unreachable";
		});
		if (listening)
		{
			process.EmitListening();
		}

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// A client going away is not a timeout, whether or not node was connected yet.
		await Assert.ThrowsAsync<TaskCanceledException>(
			() => instance.InvokeExportAsync<string>(cts.Token, "module.js", null!).WaitAsync(Deadline));
	}

	[Fact]
	public async Task Exit_before_Listening_fails_waiting_callers_fast()
	{
		// B1: nothing completed the ready signal when node died, so a waiting caller sat out the whole
		// invocation timeout - and with the timeout disabled, as here, it waited forever.
		using var instance = new TestNodeInstance(process, logger, invocationTimeoutMilliseconds: 0);
		var call = instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!);
		Assert.False(call.IsCompleted);

		process.Exit();

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(() => call.WaitAsync(Deadline));
		Assert.StartsWith("Node process exited before it was ready to accept invocations.", ex.Message);
		Assert.True(ex.NodeInstanceUnavailable);
		Assert.False(ex.AllowConnectionDraining);
	}

	[Fact]
	public async Task Exit_after_Listening_leaves_the_established_connection_alone()
	{
		using var instance = new TestNodeInstance(process, logger);
		process.EmitListening();
		Assert.Equal("ok", await instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!));

		process.Exit();

		var ex = await Assert.ThrowsAsync<NodeInvocationException>(
			() => instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!));
		Assert.StartsWith("The Node process has exited", ex.Message);
	}

	[Fact]
	public async Task Waiting_invocations_do_not_resume_on_nodes_output_thread()
	{
		// B6: the ready signal is completed from node's stdout reader. Without RunContinuationsAsynchronously
		// every waiting invocation ran its whole RPC on that thread, and node's output went unread meanwhile.
		var invokeThread = 0;
		using var instance = new TestNodeInstance(process, logger, invoke: (_, _) =>
		{
			invokeThread = Environment.CurrentManagedThreadId;
			return Task.FromResult<object>("ok");
		});

		// Started off the test's synchronization context, so nothing but the continuation options decides
		// where it resumes. Task.Run hands back the invocation once it is parked on the ready signal.
		var call = await Task.Run(() => Task.FromResult(instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!)));
		Assert.False(call.IsCompleted);

		var readerThread = 0;
		var reader = new Thread(() =>
		{
			readerThread = Environment.CurrentManagedThreadId;
			process.EmitListening();
		});
		reader.Start();
		Assert.True(reader.Join(Deadline));

		Assert.Equal("ok", await call.WaitAsync(Deadline));
		Assert.NotEqual(0, invokeThread);
		Assert.NotEqual(readerThread, invokeThread);
	}

	[Fact]
	public void Dispose_kills_a_live_process_tree_and_disposes_it()
	{
		var projectPath = CreateTempDirectory();
		try
		{
			var instance = new TestNodeInstance(process, logger, projectPath: projectPath, watchFileExtensions: [".js"]);
			var entryPoint = EntryPointOf(process);
			Assert.True(File.Exists(entryPoint));

			instance.Dispose();
			instance.Dispose();

			// B6: Kill() used to leave node's own children running, and the Process was never disposed.
			Assert.Equal(1, process.KillCount);
			Assert.True(process.KilledEntireTree);
			Assert.True(process.Disposed);
			Assert.False(File.Exists(entryPoint), "The entry-point temp file outlived the instance.");
		}
		finally
		{
			Directory.Delete(projectPath, recursive: true);
		}
	}

	[Fact]
	public void Dispose_does_not_kill_a_process_that_already_exited()
	{
		var instance = new TestNodeInstance(process, logger);
		process.Exit();

		instance.Dispose();

		Assert.Equal(0, process.KillCount);
		Assert.True(process.Disposed);
	}

	[Fact]
	public void The_launch_uses_the_composed_start_info()
	{
		using var instance = new TestNodeInstance(process, logger, launchWithDebugging: true);

		Assert.Equal("node", process.StartInfo!.FileName);
		Assert.StartsWith("--inspect=9229 ", process.StartInfo.Arguments);
		Assert.EndsWith("--custom-arg", process.StartInfo.Arguments);
		Assert.Equal("module.exports = {};", File.ReadAllText(EntryPointOf(process)));
	}

	internal static string EntryPointOf(FakeNodeProcess process)
		=> Regex.Match(process.StartInfo!.Arguments, "\"([^\"]+)\"").Groups[1].Value;

	internal static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "node-instance-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	internal sealed class TestNodeInstance(
		FakeNodeProcess process,
		ILogger logger,
		int invocationTimeoutMilliseconds = 60 * 1000,
		bool launchWithDebugging = false,
		string? projectPath = null,
		string[]? watchFileExtensions = null,
		Func<NodeInvocationInfo, CancellationToken, Task<object>>? invoke = null)
		: OutOfProcessNodeInstance(
			"module.exports = {};",
			projectPath ?? Path.GetTempPath(),
			watchFileExtensions ?? [],
			"--custom-arg",
			CancellationToken.None,
			logger,
			new Dictionary<string, string>(),
			invocationTimeoutMilliseconds,
			launchWithDebugging,
			9229,
			"node",
			process.Start)
	{
		private readonly Func<NodeInvocationInfo, CancellationToken, Task<object>> invoke =
			invoke ?? ((_, _) => Task.FromResult<object>("ok"));

		protected override async Task<T> InvokeExportAsync<T>(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
			=> (T)await invoke(invocationInfo, cancellationToken);
	}
}

/// <summary>
/// The restart-on-file-change path, through a real <see cref="FileSystemWatcher"/> on a dedicated temp
/// directory. Timing-sensitive, so kept out of the parallel collections.
/// </summary>
[Collection(NodeServicesGlobalStateCollection.Name)]
public class OutOfProcessNodeInstanceWatcherTests : IDisposable
{
	private readonly FakeNodeProcess process = new();
	private readonly CapturingNodeLogger logger = new();
	private readonly string projectPath = OutOfProcessNodeInstanceTests.CreateTempDirectory();

	public void Dispose() => Directory.Delete(projectPath, recursive: true);

	[Fact]
	public async Task Watched_file_change_forces_restart()
	{
		using var instance = NewInstance();

		File.WriteAllText(Path.Combine(projectPath, "notes.txt"), "not watched");
		File.WriteAllText(Path.Combine(projectPath, "app.js"), "watched");

		var ex = await WaitForRestartAsync(instance);
		Assert.True(ex.NodeInstanceUnavailable);
		Assert.True(ex.AllowConnectionDraining);
		Assert.Contains(logger.Entries, e => e.Message == "Node will restart because file changed: " + Path.Combine(projectPath, "app.js"));
	}

	[Fact]
	public async Task Rename_forces_restart()
	{
		var before = Path.Combine(projectPath, "app.txt");
		File.WriteAllText(before, "renamed into a watched extension");
		using var instance = NewInstance();

		File.Move(before, Path.Combine(projectPath, "app.js"));

		await WaitForRestartAsync(instance);
		Assert.Contains(logger.Entries, e => e.Message == "Node will restart because file changed: " + before);
	}

	private OutOfProcessNodeInstanceTests.TestNodeInstance NewInstance()
	{
		var instance = new OutOfProcessNodeInstanceTests.TestNodeInstance(process, logger, projectPath: projectPath, watchFileExtensions: [".js"]);
		process.EmitListening();
		return instance;
	}

	/// <summary>Invokes until the instance reports that it needs to restart, for at most 5 seconds.</summary>
	private static async Task<NodeInvocationException> WaitForRestartAsync(OutOfProcessNodeInstance instance)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (true)
		{
			try
			{
				await instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!);
			}
			catch (NodeInvocationException ex)
			{
				Assert.StartsWith("The Node process needs to restart", ex.Message);
				return ex;
			}

			Assert.True(DateTime.UtcNow < deadline, "The file change never forced a restart.");
			await Task.Delay(20);
		}
	}
}
