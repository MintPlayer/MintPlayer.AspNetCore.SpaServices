// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering.Internals;

/// <summary>
/// Ensures child processes are terminated when the parent process exits.
/// On Windows, uses a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so the OS
/// automatically kills children even on abrupt termination (e.g., stopping the VS debugger).
/// On all platforms, also registers an <see cref="AppDomain.ProcessExit"/> handler as a fallback.
/// </summary>
internal static class ProcessTracker
{
	private static readonly List<Process> s_trackedProcesses = new();
	private static readonly nint s_jobHandle;

	static ProcessTracker()
	{
		if (OperatingSystem.IsWindows())
		{
			s_jobHandle = CreateJobObject(nint.Zero, null);
			if (s_jobHandle != nint.Zero)
			{
				var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
				{
					BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
					{
						LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
					}
				};

				var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
				var infoPtr = Marshal.AllocHGlobal(length);
				try
				{
					Marshal.StructureToPtr(info, infoPtr, false);
					SetInformationJobObject(s_jobHandle, 9 /* ExtendedLimitInformation */, infoPtr, (uint)length);
				}
				finally
				{
					Marshal.FreeHGlobal(infoPtr);
				}
			}
		}

		AppDomain.CurrentDomain.ProcessExit += (_, _) => KillTrackedProcesses();
	}

	public static void AddProcess(Process process)
	{
		if (OperatingSystem.IsWindows() && s_jobHandle != nint.Zero)
		{
			AssignProcessToJobObject(s_jobHandle, process.Handle);
		}

		lock (s_trackedProcesses)
		{
			s_trackedProcesses.Add(process);
		}
	}

	private static void KillTrackedProcesses()
	{
		lock (s_trackedProcesses)
		{
			foreach (var process in s_trackedProcesses)
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill(entireProcessTree: true);
					}
				}
				catch
				{
					// Best effort — process may have already exited
				}
			}

			s_trackedProcesses.Clear();
		}
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(nint hJob, int jobObjectInfoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}
}
