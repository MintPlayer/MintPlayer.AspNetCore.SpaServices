// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace MintPlayer.AspNetCore.NodeServices.HostingModels;

/// <summary>
/// The parts of a launched Node process that <see cref="OutOfProcessNodeInstance"/> uses.
/// <para>
/// Internal seam, so the handshake, logging, timeout and restart logic can be tested without node.
/// <see cref="SystemNodeProcess"/> is the only production implementation.
/// </para>
/// </summary>
internal interface INodeProcess : IDisposable
{
	/// <summary>Whether the process has exited.</summary>
	bool HasExited { get; }

	/// <summary>Raised once the process has exited.</summary>
	event EventHandler? Exited;

	/// <summary>Terminates the process, and optionally every process it started.</summary>
	void Kill(bool entireProcessTree);

	/// <summary>
	/// Starts delivering stdout and stderr, line by line. A <c>null</c> line signals the end of that stream.
	/// </summary>
	void BeginReadLines(Action<string?> onStdoutLine, Action<string?> onStderrLine);
}
