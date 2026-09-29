using System.Diagnostics;
using System.Text;
using MintPlayer.AspNetCore.SpaServices.Npm;

namespace MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;

/// <summary>Replays a fixed stdout/stderr script as if it were a package-manager process.</summary>
/// <remarks>
/// Shared rather than private to one suite: the Angular CLI handshake and the SSR bundle build both
/// start their script through an <see cref="IProcessLauncher"/>.
/// </remarks>
internal sealed class ScriptedLauncher(string stdOut, string stdErr) : IProcessLauncher
{
	public ProcessStartInfo? LastStartInfo { get; private set; }

	/// <summary>The child handed out by the most recent <see cref="Start"/>.</summary>
	public ScriptedChild? LastChild { get; private set; }

	/// <summary>
	/// What the child reports for <see cref="IChildProcess.HasExited"/> until it is killed. The
	/// default, true, matches the script having run to completion; false models a <c>--watch</c>
	/// process that is still alive after its output has been read.
	/// </summary>
	public bool ChildHasExited { get; init; } = true;

	public IChildProcess Start(ProcessStartInfo startInfo)
	{
		LastStartInfo = startInfo;
		return LastChild = new ScriptedChild(stdOut, stdErr, ChildHasExited);
	}

	internal sealed class ScriptedChild(string stdOut, string stdErr, bool hasExited) : IChildProcess
	{
		public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdOut)));

		public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdErr)));

		public bool HasExited => hasExited || Killed;

		public bool Killed { get; private set; }

		public bool Disposed { get; private set; }

		public void Kill(bool entireProcessTree) => Killed = true;

		public void Dispose() => Disposed = true;
	}
}
