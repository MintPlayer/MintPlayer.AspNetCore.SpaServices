// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Diagnostics;

namespace MintPlayer.AspNetCore.NodeServices.HostingModels;

/// <summary>
/// <see cref="INodeProcess"/> over a real <see cref="Process"/>. A thin adapter: the launch and the
/// event wiring are what <see cref="OutOfProcessNodeInstance"/> used to do inline.
/// </summary>
internal sealed class SystemNodeProcess : INodeProcess
{
	private readonly Process _process;

	private SystemNodeProcess(Process process)
	{
		_process = process;
	}

	/// <summary>Starts the process described by <paramref name="startInfo"/>. Throws if it cannot be started.</summary>
	public static INodeProcess Start(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)!;

		// On Mac at least, a killed child process is left open as a zombie until the parent
		// captures its exit code. We don't need the exit code for this process, and don't want
		// to use process.WaitForExit() explicitly (we'd have to block the thread until it really
		// has exited), but we don't want to leave zombies lying around either. It's sufficient
		// to use process.EnableRaisingEvents so that .NET will grab the exit code and let the
		// zombie be cleaned away without having to block our thread.
		process.EnableRaisingEvents = true;

		return new SystemNodeProcess(process);
	}

	public bool HasExited => _process.HasExited;

	public event EventHandler? Exited
	{
		add => _process.Exited += value;
		remove => _process.Exited -= value;
	}

	public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

	public void BeginReadLines(Action<string?> onStdoutLine, Action<string?> onStderrLine)
	{
		_process.OutputDataReceived += (sender, evt) => onStdoutLine(evt.Data);
		_process.ErrorDataReceived += (sender, evt) => onStderrLine(evt.Data);

		_process.BeginOutputReadLine();
		_process.BeginErrorReadLine();
	}

	public void Dispose() => _process.Dispose();
}
