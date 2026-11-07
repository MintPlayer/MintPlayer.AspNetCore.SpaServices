using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.NodeServices;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The <see cref="NodeServicesOptions"/> the <c>OutOfProcessNodeInstance</c> subclasses in these tests are built from:
/// no host, so no <c>NODE_ENV</c>, no stopping token and no watched files unless asked for.
/// </summary>
internal static class TestNodeOptions
{
	public static NodeServicesOptions Create(
		string nodePath,
		ILogger? logger = null,
		int invocationTimeoutMilliseconds = 1000,
		bool launchWithDebugging = false,
		int debuggingPort = 0,
		string? projectPath = null,
		string[]? watchFileExtensions = null)
		=> new(new ServiceCollection().BuildServiceProvider())
		{
			NodePath = nodePath,
			NodeInstanceOutputLogger = logger ?? NullLogger.Instance,
			InvocationTimeoutMilliseconds = invocationTimeoutMilliseconds,
			LaunchWithDebugging = launchWithDebugging,
			DebuggingPort = debuggingPort,
			ProjectPath = projectPath ?? Path.GetTempPath(),
			WatchFileExtensions = watchFileExtensions ?? [],
		};
}
