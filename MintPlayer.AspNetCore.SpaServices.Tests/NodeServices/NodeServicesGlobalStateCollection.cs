using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// NodeServices tests that touch process-global state: finalizers driven by <c>GC.Collect</c>, and real
/// <see cref="FileSystemWatcher"/>s whose timing a busy parallel run would skew. They run on their own,
/// after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NodeServicesGlobalStateCollection
{
	public const string Name = "NodeServices global state";
}
