using System.Diagnostics;

namespace MintPlayer.AspNetCore.SpaServices.Npm;

/// <summary>
/// The five members of <see cref="Process"/> that the script runner actually uses.
/// </summary>
/// <remarks>
/// This exists so the runner can be driven without a package manager on the machine. It is
/// deliberately the smallest possible surface: anything wider would be a second, worse
/// <see cref="Process"/> rather than a seam.
/// </remarks>
internal interface IChildProcess : IDisposable
{
	StreamReader StandardOutput { get; }

	StreamReader StandardError { get; }

	bool HasExited { get; }

	void Kill(bool entireProcessTree);
}

/// <summary>Starts a child process. The default implementation is the real <see cref="Process"/>.</summary>
internal interface IProcessLauncher
{
	IChildProcess Start(ProcessStartInfo startInfo);
}

/// <summary>
/// The shipped launcher: starts a real process, enables exit events, and registers it with
/// <see cref="ProcessTracker"/> so it dies with the host.
/// </summary>
internal sealed class SystemProcessLauncher : IProcessLauncher
{
	public static readonly SystemProcessLauncher Instance = new();

	public IChildProcess Start(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)!;

		// See equivalent comment in OutOfProcessNodeInstance.cs for why
		process.EnableRaisingEvents = true;

		ProcessTracker.AddProcess(process);

		return new SystemChildProcess(process);
	}

	private sealed class SystemChildProcess(Process process) : IChildProcess
	{
		public StreamReader StandardOutput => process.StandardOutput;

		public StreamReader StandardError => process.StandardError;

		public bool HasExited => process.HasExited;

		public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

		public void Dispose() => process.Dispose();
	}
}
