using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// Drives the real launch path of <see cref="OutOfProcessNodeInstance"/> with a node executable that
/// does not exist, so <see cref="Process.Start(ProcessStartInfo)"/> fails and no process is ever created.
/// </summary>
public class OutOfProcessNodeInstanceLaunchTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void A_missing_node_executable_fails_with_a_descriptive_error(bool launchWithDebugging)
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new MissingNodeInstance(launchWithDebugging, _ => { }));

		Assert.StartsWith("Failed to start Node process", ex.Message);
		Assert.NotNull(ex.InnerException);
	}

	[Fact]
	public void A_failed_launch_deletes_the_entry_point_temp_file()
	{
		// B2: the constructor wrote the entry point to disk and then threw, so nobody held a reference
		// that could have disposed it.
		string? entryPoint = null;

		Assert.Throws<InvalidOperationException>(() => new MissingNodeInstance(false, path => entryPoint = path));

		Assert.NotNull(entryPoint);
		Assert.False(File.Exists(entryPoint), "The entry-point temp file outlived the failed launch.");
	}

	[Fact]
	public void HttpNodeInstance_launches_the_configured_node_with_the_http_entry_point()
	{
		var options = new NodeServicesOptions(new ServiceCollection().BuildServiceProvider())
		{
			NodePath = MissingNodePath(),
			ProjectPath = Path.GetTempPath(),
		};

		var ex = Assert.Throws<InvalidOperationException>(() => new HttpNodeInstance(options));

		Assert.StartsWith("Failed to start Node process", ex.Message);
	}

	[Fact]
	public void UseHttpHosting_installs_a_factory_that_builds_an_HttpNodeInstance()
	{
		var options = new NodeServicesOptions(new ServiceCollection().BuildServiceProvider())
		{
			NodeInstanceFactory = null!,
			NodePath = MissingNodePath(),
			ProjectPath = Path.GetTempPath(),
		};

		options.UseHttpHosting();

		// The only observable trace of HttpNodeInstance's construction without node is its launch failure.
		var ex = Assert.Throws<InvalidOperationException>(() => options.NodeInstanceFactory());
		Assert.StartsWith("Failed to start Node process", ex.Message);
	}

	internal static string MissingNodePath() => "no-such-node-" + Guid.NewGuid().ToString("N");

	private sealed class MissingNodeInstance(bool launchWithDebugging, Action<string> onEntryPoint)
		: OutOfProcessNodeInstance(
			"module.exports = {};",
			Path.GetTempPath(),
			[],
			string.Empty,
			CancellationToken.None,
			NullLogger.Instance,
			new Dictionary<string, string>(),
			1000,
			launchWithDebugging,
			9229,
			MissingNodePath())
	{
		// A field initializer, not a constructor body: the base constructor calls the override below
		// before this class's own constructor body would run.
		private readonly Action<string> onEntryPoint = onEntryPoint;

		protected override ProcessStartInfo PrepareNodeProcessStartInfo(
			string entryPointFilename, string projectPath, string commandLineArguments,
			IDictionary<string, string> environmentVars, bool launchWithDebugging, int debuggingPort, string nodePath)
		{
			onEntryPoint(entryPointFilename);
			return base.PrepareNodeProcessStartInfo(
				entryPointFilename, projectPath, commandLineArguments, environmentVars, launchWithDebugging, debuggingPort, nodePath);
		}

		protected override Task<T> InvokeExportAsync<T>(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}
}

/// <summary>
/// Finalizer paths. <c>GC.Collect</c> is process-global, so these run outside the parallel collections.
/// </summary>
[Collection(NodeServicesGlobalStateCollection.Name)]
public class NodeServicesFinalizerTests
{
	[Fact]
	public void The_finalizer_of_a_never_launched_instance_runs_without_a_process()
	{
		FinalizerProbe.Finalized = false;

		AbandonAFailedInstance();
		GC.Collect();
		GC.WaitForPendingFinalizers();

		Assert.True(FinalizerProbe.Finalized);
	}

	[Fact]
	public void The_StringAsTempFile_finalizer_deletes_the_file()
	{
		var fileName = AbandonATempFile();
		GC.Collect();
		GC.WaitForPendingFinalizers();

		Assert.False(File.Exists(fileName), "The finalizer did not delete the temp file.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void AbandonAFailedInstance()
		=> Assert.Throws<InvalidOperationException>(() => new FinalizerProbe());

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string AbandonATempFile()
	{
		var file = new StringAsTempFile("abandoned", CancellationToken.None);
		Assert.True(File.Exists(file.FileName));
		return file.FileName;
	}

	private sealed class FinalizerProbe()
		: OutOfProcessNodeInstance(
			"module.exports = {};",
			Path.GetTempPath(),
			[],
			string.Empty,
			CancellationToken.None,
			NullLogger.Instance,
			new Dictionary<string, string>(),
			1000,
			false,
			0,
			OutOfProcessNodeInstanceLaunchTests.MissingNodePath())
	{
		public static volatile bool Finalized;

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (!disposing)
			{
				Finalized = true;
			}
		}

		protected override Task<T> InvokeExportAsync<T>(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}
}
