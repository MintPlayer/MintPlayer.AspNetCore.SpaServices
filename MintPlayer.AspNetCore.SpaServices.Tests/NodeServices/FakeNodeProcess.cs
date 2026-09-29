using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.NodeServices.HostingModels;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// Stands in for a launched node process. The test plays node's part: it writes the lines node would
/// write and decides when the process exits.
/// </summary>
internal sealed class FakeNodeProcess : INodeProcess
{
	private Action<string?>? stdout;
	private Action<string?>? stderr;
	private volatile bool hasExited;

	/// <summary>What the instance asked to launch.</summary>
	public ProcessStartInfo? StartInfo { get; private set; }

	public bool HasExited => hasExited;

	public event EventHandler? Exited;

	public int KillCount { get; private set; }

	public bool KilledEntireTree { get; private set; }

	public bool Disposed { get; private set; }

	/// <summary>The launch seam: records the start info and hands out this process.</summary>
	public INodeProcess Start(ProcessStartInfo startInfo)
	{
		StartInfo = startInfo;
		return this;
	}

	public void BeginReadLines(Action<string?> onStdoutLine, Action<string?> onStderrLine)
	{
		stdout = onStdoutLine;
		stderr = onStderrLine;
	}

	public void EmitStdout(string? line) => (stdout ?? throw new InvalidOperationException("Reading has not begun."))(line);

	public void EmitStderr(string? line) => (stderr ?? throw new InvalidOperationException("Reading has not begun."))(line);

	/// <summary>The node-side ready signal.</summary>
	public void EmitListening() => EmitStdout("[MintPlayer.AspNetCore.NodeServices:Listening]");

	public void Exit()
	{
		hasExited = true;
		Exited?.Invoke(this, EventArgs.Empty);
	}

	public void Kill(bool entireProcessTree)
	{
		KillCount++;
		KilledEntireTree = entireProcessTree;
		Exit();
	}

	public void Dispose() => Disposed = true;
}

/// <summary>Records what the node instance logs.</summary>
internal sealed class CapturingNodeLogger : ILogger
{
	public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		=> Entries.Enqueue((logLevel, formatter(state, exception)));
}
