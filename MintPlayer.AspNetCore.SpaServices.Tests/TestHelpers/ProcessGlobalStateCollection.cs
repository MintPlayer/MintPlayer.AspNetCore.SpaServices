using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;

/// <summary>
/// For tests that touch state shared by the whole test process: <c>Console.SetOut</c>, or
/// <c>ProcessTracker.KillTrackedProcesses</c>, which kills every process any test has started.
/// xUnit runs a collection with parallelization disabled on its own, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection
{
	public const string Name = "Process-global state";
}
