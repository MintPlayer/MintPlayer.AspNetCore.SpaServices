using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The real <see cref="SystemNodeProcess"/> adapter, over OS built-ins only (cmd / sh / cat / whoami), never node.
/// Every wait is bounded, so a hang fails the test instead of the run.
/// </summary>
public class SystemNodeProcessTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task Delivers_stdout_and_stderr_lines_then_the_end_of_each_stream()
	{
		var startInfo = OperatingSystem.IsWindows()
			? new ProcessStartInfo("cmd.exe", "/c echo out& echo err 1>&2")
			: new ProcessStartInfo("/bin/sh", "-c \"echo out; echo err 1>&2\"");
		var stdout = new ConcurrentQueue<string>();
		var stderr = new ConcurrentQueue<string>();
		var stdoutEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stderrEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		using var process = SystemNodeProcess.Start(Redirected(startInfo));
		EventHandler onExited = (_, _) => { };
		process.Exited += onExited;
		process.Exited -= onExited;
		process.BeginReadLines(
			line => { if (line == null) stdoutEnded.TrySetResult(); else stdout.Enqueue(line); },
			line => { if (line == null) stderrEnded.TrySetResult(); else stderr.Enqueue(line); });

		await Task.WhenAll(stdoutEnded.Task, stderrEnded.Task).WaitAsync(Deadline);

		Assert.Contains("out", stdout.Select(l => l.Trim()));
		Assert.Contains("err", stderr.Select(l => l.Trim()));
		Assert.True(await PollAsync(() => process.HasExited), "The process never reported that it exited.");
	}

	[Fact]
	public async Task Kills_a_blocked_process_and_raises_Exited()
	{
		// Both block reading the redirected stdin, which nobody writes to or closes.
		var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "cat");
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		using var process = SystemNodeProcess.Start(Redirected(startInfo));
		process.Exited += (_, _) => exited.TrySetResult();
		process.BeginReadLines(_ => { }, _ => { });
		Assert.False(process.HasExited);

		process.Kill(entireProcessTree: true);

		await exited.Task.WaitAsync(Deadline);
		Assert.True(process.HasExited);
	}

	[Fact]
	public async Task The_public_constructors_launch_through_SystemNodeProcess()
	{
		// A stand-in "node" that rejects the entry-point arguments and exits at once. That exercises the whole
		// real path - public constructor, launch, stream wiring, Exited - and, through B1, surfaces as a fast
		// failure of the pending invocation rather than a timeout.
		var options = new NodeServicesOptions(new ServiceCollection().BuildServiceProvider())
		{
			NodePath = StandInNodePath,
			ProjectPath = Path.GetTempPath(),
			WatchFileExtensions = [],
			InvocationTimeoutMilliseconds = 0,
		};
		options.UseHttpHosting();

		using var instance = options.NodeInstanceFactory();
		var ex = await Assert.ThrowsAnyAsync<NodeInvocationException>(
			() => instance.InvokeExportAsync<string>(CancellationToken.None, "module.js", null!).WaitAsync(Deadline));

		// Either the exit beat the invocation (HasExited) or the invocation was already waiting (B1).
		Assert.True(ex.NodeInstanceUnavailable);
	}

	[Fact]
	public void The_public_base_constructor_launches_through_SystemNodeProcess()
	{
		// What a third-party subclass of the public abstract class goes through.
		using var instance = new ThirdPartyNodeInstance();
	}

	private sealed class ThirdPartyNodeInstance()
		: OutOfProcessNodeInstance(
			"module.exports = {};",
			Path.GetTempPath(),
			[],
			string.Empty,
			CancellationToken.None,
			Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
			new Dictionary<string, string>(),
			1000,
			false,
			0,
			StandInNodePath)
	{
		protected override Task<T> InvokeExportAsync<T>(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}

	private static string StandInNodePath => OperatingSystem.IsWindows() ? "whoami.exe" : "cat";

	private static ProcessStartInfo Redirected(ProcessStartInfo startInfo)
	{
		startInfo.UseShellExecute = false;
		startInfo.RedirectStandardInput = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		return startInfo;
	}

	private static async Task<bool> PollAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + Deadline;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				return false;
			}

			await Task.Delay(20);
		}

		return true;
	}
}
