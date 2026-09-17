using System.Runtime.InteropServices;
using MintPlayer.AspNetCore.SpaServices.Npm;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Npm;

/// <summary>
/// The command line the package-manager process is launched with. Constructing the runner spawns the
/// process, so this composition is only reachable through the extracted builder.
/// </summary>
public class NodeScriptRunnerStartInfoTests
{
	private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	[Fact]
	public void Rejects_a_missing_working_directory()
	{
		var ex = Assert.Throws<ArgumentException>(() => NodeScriptRunner.BuildStartInfo("", "build", null, null, "npm"));

		Assert.Equal("workingDirectory", ex.ParamName);
	}

	[Fact]
	public void Rejects_a_missing_script_name()
	{
		var ex = Assert.Throws<ArgumentException>(() => NodeScriptRunner.BuildStartInfo("C:/app", "", null, null, "npm"));

		Assert.Equal("scriptName", ex.ParamName);
	}

	[Fact]
	public void Rejects_a_missing_package_manager_command()
	{
		var ex = Assert.Throws<ArgumentException>(() => NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, ""));

		Assert.Equal("pkgManagerCommand", ex.ParamName);
	}

	[Fact]
	public void Redirects_all_three_streams_and_does_not_use_the_shell()
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, "npm");

		// UseShellExecute would prevent stdio capture, which is the whole point of the runner.
		Assert.False(info.UseShellExecute);
		Assert.True(info.RedirectStandardInput);
		Assert.True(info.RedirectStandardOutput);
		Assert.True(info.RedirectStandardError);
		Assert.Equal("C:/app", info.WorkingDirectory);
	}

	[Fact]
	public void Invokes_the_package_manager_through_cmd_on_windows()
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, "npm");

		if (OnWindows)
		{
			// npm is a .cmd shim on Windows and cannot be executed directly while still capturing stdio.
			Assert.Equal("cmd", info.FileName);
			Assert.StartsWith("/c npm run build", info.Arguments);
		}
		else
		{
			Assert.Equal("npm", info.FileName);
			Assert.StartsWith("run build", info.Arguments);
		}
	}

	[Fact]
	public void Passes_script_arguments_after_a_double_dash()
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", "--configuration production", null, "npm");

		// The "--" is what makes npm forward the rest to the script instead of consuming it itself.
		Assert.Contains("run build -- --configuration production", info.Arguments);
	}

	[Fact]
	public void Tolerates_null_arguments()
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, "npm");

		Assert.Contains("run build --", info.Arguments);
	}

	[Theory]
	[InlineData("yarn")]
	[InlineData("pnpm")]
	public void Honours_an_alternative_package_manager(string pkgManager)
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, pkgManager);

		if (OnWindows)
		{
			// Windows runs everything through "cmd /c", so the package manager is part of the
			// argument string rather than the executable.
			Assert.Equal("cmd", info.FileName);
			Assert.Contains($"/c {pkgManager} run build", info.Arguments);
		}
		else
		{
			// Everywhere else it is the executable itself, and the arguments start at "run".
			Assert.Equal(pkgManager, info.FileName);
			Assert.StartsWith("run build", info.Arguments);
			Assert.DoesNotContain(pkgManager, info.Arguments);
		}
	}

	[Fact]
	public void Copies_environment_variables_onto_the_start_info()
	{
		var envVars = new Dictionary<string, string>
		{
			["PORT"] = "4200",
			["NODE_ENV"] = "development",
		};

		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, envVars, "npm");

		Assert.Equal("4200", info.Environment["PORT"]);
		Assert.Equal("development", info.Environment["NODE_ENV"]);
	}

	[Fact]
	public void Tolerates_a_null_environment_dictionary()
	{
		var info = NodeScriptRunner.BuildStartInfo("C:/app", "build", null, null, "npm");

		Assert.NotNull(info.Environment);
	}
}
