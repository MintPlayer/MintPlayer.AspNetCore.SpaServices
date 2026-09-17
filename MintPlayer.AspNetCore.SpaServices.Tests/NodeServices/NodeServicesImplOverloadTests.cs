using System.Reflection;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The <c>NodeServicesImpl</c> entry points that are pure delegation, plus the delayed-disposal
/// exception relay. Everything runs against a fake <see cref="INodeInstance"/>; no node is launched.
/// </summary>
public class NodeServicesImplOverloadTests
{
	private static NodeServicesImpl Create(Func<NodeInvocationInfo, Task<object>> invoke)
		=> new(() => new RecordingNodeInstance(invoke));

	[Fact]
	public async Task InvokeAsync_with_a_cancellation_token_reaches_the_instance()
	{
		NodeInvocationInfo seen = null!;
		using var services = Create(info => { seen = info; return Task.FromResult<object>("ok"); });

		var result = await services.InvokeAsync<string>(CancellationToken.None, "some-module", 1, "two");

		Assert.Equal("ok", result);
		Assert.Equal("some-module", seen.ModuleName);
		// The token-taking InvokeAsync forwards a null export name - it is the "default export" form.
		Assert.Null(seen.ExportedFunctionName);
		Assert.Equal([1, "two"], seen.Args);
	}

	[Fact]
	public async Task InvokeExportAsync_forwards_the_export_name()
	{
		NodeInvocationInfo seen = null!;
		using var services = Create(info => { seen = info; return Task.FromResult<object>("ok"); });

		await services.InvokeExportAsync<string>("some-module", "namedExport", 42);

		Assert.Equal("some-module", seen.ModuleName);
		Assert.Equal("namedExport", seen.ExportedFunctionName);
		Assert.Equal([42], seen.Args);
	}

	[Fact]
	public async Task InvokeExportAsync_with_a_cancellation_token_forwards_both()
	{
		NodeInvocationInfo seen = null!;
		using var services = Create(info => { seen = info; return Task.FromResult<object>("ok"); });

		using var cts = new CancellationTokenSource();
		await services.InvokeExportAsync<string>(cts.Token, "some-module", "namedExport");

		Assert.Equal("some-module", seen.ModuleName);
		Assert.Equal("namedExport", seen.ExportedFunctionName);
		Assert.Empty(seen.Args);
	}

	[Fact]
	public async Task A_failed_delayed_disposal_is_rethrown_to_the_next_caller()
	{
		using var services = Create(_ => Task.FromResult<object>("ok"));
		var boom = new InvalidOperationException("dispose blew up");
		SetDelayedDisposalException(services, boom);

		// Nothing awaits the delayed disposal task, so the only way its failure can surface is on the
		// next invocation. That relay is what this asserts.
		var ex = await Assert.ThrowsAsync<AggregateException>(() => services.InvokeAsync<string>("module"));

		Assert.Same(boom, ex.InnerException);
	}

	[Fact]
	public async Task The_delayed_disposal_exception_is_reported_only_once()
	{
		using var services = Create(_ => Task.FromResult<object>("ok"));
		SetDelayedDisposalException(services, new InvalidOperationException("dispose blew up"));

		await Assert.ThrowsAsync<AggregateException>(() => services.InvokeAsync<string>("module"));

		// The field is cleared as it is thrown, so a later call is not poisoned by a stale failure.
		Assert.Equal("ok", await services.InvokeAsync<string>("module"));
	}

	private static void SetDelayedDisposalException(NodeServicesImpl services, Exception exception)
	{
		var field = typeof(NodeServicesImpl).GetField("_instanceDelayedDisposalException", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("_instanceDelayedDisposalException is gone - update this test.");
		field.SetValue(services, exception);
	}

	private sealed class RecordingNodeInstance(Func<NodeInvocationInfo, Task<object>> invoke) : INodeInstance
	{
		public async Task<T> InvokeExportAsync<T>(CancellationToken cancellationToken, string moduleName, string exportNameOrNull, params object[] args)
		{
			var result = await invoke(new NodeInvocationInfo
			{
				ModuleName = moduleName,
				ExportedFunctionName = exportNameOrNull,
				Args = args,
			});
			return (T)result;
		}

		public void Dispose() { }
	}
}
