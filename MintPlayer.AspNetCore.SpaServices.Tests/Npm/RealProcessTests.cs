using System.Diagnostics;
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Npm;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Npm;

/// <summary>
/// The real-process adapters behind the launcher seam - <c>SystemProcessLauncher</c>, the public
/// <c>NodeScriptRunner</c> constructor and <c>ProcessTracker</c> - driven with OS built-ins only:
/// <c>cmd.exe</c> on Windows, <c>cat</c> and <c>echo</c> elsewhere. No node, no npm.
/// </summary>
/// <remarks>
/// In the non-parallel collection because <see cref="ProcessTracker.KillTrackedProcesses"/> kills
/// every process any test has started. Every wait is a bounded poll, so a process that refuses to
/// exit fails the test rather than hanging the run.
/// </remarks>
[Collection(ProcessGlobalStateCollection.Name)]
public class RealProcessTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

	/// <summary>
	/// A process that runs until it is killed: both of these sit reading a redirected stdin that
	/// nothing ever writes to or closes.
	/// </summary>
	private static ProcessStartInfo RunsUntilKilled()
		=> new(OperatingSystem.IsWindows() ? "cmd.exe" : "cat")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

	private static ProcessStartInfo ExitsAtOnce()
		=> OperatingSystem.IsWindows()
			? new("cmd.exe", "/c exit 0") { UseShellExecute = false, RedirectStandardOutput = true }
			: new("echo") { UseShellExecute = false, RedirectStandardOutput = true };

	private static async Task WaitUntil(Func<bool> condition, string what)
	{
		using var timeout = new CancellationTokenSource(Deadline);
		while (!condition())
		{
			if (timeout.IsCancellationRequested)
				throw new TimeoutException($"Timed out waiting until {what}.");

			await Task.Delay(10);
		}
	}

	[Fact]
	public async Task SystemProcessLauncher_starts_kills_and_disposes_a_real_process()
	{
		using var child = SystemProcessLauncher.Instance.Start(RunsUntilKilled());

		Assert.False(child.HasExited);
		Assert.NotNull(child.StandardOutput);
		Assert.NotNull(child.StandardError);

		child.Kill(entireProcessTree: true);

		await WaitUntil(() => child.HasExited, "the killed process has exited");
	}

	[Fact]
	public async Task Public_ctor_launches_the_package_manager_for_real()
	{
		// "echo" stands in for the package manager, so the command line the runner composes is
		// printed back instead of run: "cmd /c echo run build -- " on Windows, "echo run build -- "
		// elsewhere.
		var runner = new NodeScriptRunner(
			Path.GetTempPath(),
			"build",
			null,
			null,
			"echo",
			new DiagnosticListener("test"),
			CancellationToken.None);

		try
		{
			var match = await runner.StdOut.WaitForMatch(new Regex("run build --")).WaitAsync(Deadline);

			Assert.True(match.Success);
		}
		finally
		{
			((IDisposable)runner).Dispose();
		}
	}

	[Fact]
	public async Task ProcessTracker_forgets_a_process_once_it_exits()
	{
		// Processes used to be added and never removed, so the static list grew with every restart.
		using var process = Process.Start(ExitsAtOnce())!;
		process.EnableRaisingEvents = true;

		ProcessTracker.AddProcess(process);

		await WaitUntil(() => !ProcessTracker.IsTracked(process), "the exited process is no longer tracked");
	}

	[Fact]
	public async Task KillTrackedProcesses_kills_a_live_process_and_skips_one_it_can_no_longer_query()
	{
		using var live = Process.Start(RunsUntilKilled())!;
		live.EnableRaisingEvents = true;
		ProcessTracker.AddProcess(live);

		// Disposed while still running, and without raising events: it stays tracked, and asking it
		// for HasExited now throws. That must not stop the sweep from reaching the live one.
		var unqueryable = Process.Start(RunsUntilKilled())!;
		var unqueryableId = unqueryable.Id;
		ProcessTracker.AddProcess(unqueryable);
		unqueryable.Dispose();

		try
		{
			ProcessTracker.KillTrackedProcesses();

			await WaitUntil(() => live.HasExited, "the tracked live process has been killed");
			Assert.False(ProcessTracker.IsTracked(live));
		}
		finally
		{
			KillIfStillRunning(unqueryableId);
		}
	}

	private static void KillIfStillRunning(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			process.Kill(entireProcessTree: true);
			process.WaitForExit(Deadline);
		}
		catch (ArgumentException)
		{
			// Already gone.
		}
		catch (InvalidOperationException)
		{
			// Exited between the lookup and the kill.
		}
	}
}
