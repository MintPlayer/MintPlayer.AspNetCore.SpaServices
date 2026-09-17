using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The decidable parts of <c>OutOfProcessNodeInstance</c>. The constructor writes a temp file,
/// launches node and starts a file watcher, so none of this is reachable through an instance - these
/// run against the extracted statics instead.
/// </summary>
public class OutOfProcessNodeInstanceStaticsTests
{
	[Fact]
	public void UnencodeNewlines_restores_the_token_the_node_shim_writes()
	{
		// The token has to match the const in OverrideStdOutputs.ts; node cannot emit a raw newline
		// inside a single stdio message without it being split into two lines.
		var result = OutOfProcessNodeInstance.UnencodeNewlines("line one__ns_newline__line two");

		Assert.Equal($"line one{Environment.NewLine}line two", result);
	}

	[Fact]
	public void UnencodeNewlines_replaces_every_occurrence()
	{
		var result = OutOfProcessNodeInstance.UnencodeNewlines("a__ns_newline__b__ns_newline__c");

		Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}c", result);
	}

	[Fact]
	public void UnencodeNewlines_passes_a_null_through()
	{
		Assert.Null(OutOfProcessNodeInstance.UnencodeNewlines(null!));
	}

	[Fact]
	public void UnencodeNewlines_leaves_ordinary_text_alone()
	{
		Assert.Equal("nothing to do", OutOfProcessNodeInstance.UnencodeNewlines("nothing to do"));
	}

	[Theory]
	[InlineData("Debugger attached.")]
	[InlineData("Debugger listening on ws://127.0.0.1:9229/")]
	[InlineData("To start debugging, open the following URL")]
	[InlineData("Warning: This is an experimental feature and could change at any time.")]
	[InlineData("For help see https://nodejs.org/en/docs/inspector")]
	[InlineData("Something mentioning chrome-devtools://foo")]
	public void IsDebuggerMessage_recognises_v8_inspector_noise(string message)
	{
		// These arrive on stderr but are not errors; misclassifying them shows spurious failures in
		// the log every time someone debugs.
		Assert.True(OutOfProcessNodeInstance.IsDebuggerMessage(message));
	}

	[Theory]
	[InlineData("TypeError: undefined is not a function")]
	[InlineData("")]
	[InlineData("Debugger")]
	[InlineData("a Debugger attached later in the line")]
	public void IsDebuggerMessage_leaves_real_stderr_alone(string message)
	{
		Assert.False(OutOfProcessNodeInstance.IsDebuggerMessage(message));
	}

	[Theory]
	[InlineData("/app/src/main.ts", true)]
	[InlineData("/app/src/main.js", true)]
	[InlineData("/app/src/styles.css", false)]
	[InlineData("/app/src/no-extension", false)]
	public void IsFilenameBeingWatched_matches_on_extension(string path, bool expected)
	{
		Assert.Equal(expected, OutOfProcessNodeInstance.IsFilenameBeingWatched(path, [".ts", ".js"]));
	}

	[Fact]
	public void IsFilenameBeingWatched_ignores_an_empty_path()
	{
		Assert.False(OutOfProcessNodeInstance.IsFilenameBeingWatched("", [".js"]));
		Assert.False(OutOfProcessNodeInstance.IsFilenameBeingWatched(null!, [".js"]));
	}

	[Fact]
	public void BuildNodeProcessStartInfo_passes_the_entry_point_and_parent_pid()
	{
		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "--flag", null!, launchWithDebugging: false, debuggingPort: 0, nodePath: "node");

		Assert.Equal("node", info.FileName);
		Assert.Contains("\"entry.js\"", info.Arguments);
		// The child watches the parent pid so it exits if the host dies without cleaning up.
		Assert.Contains($"--parentPid {Environment.ProcessId}", info.Arguments);
		Assert.Contains("--flag", info.Arguments);
		Assert.Equal("/app", info.WorkingDirectory);
		Assert.False(info.UseShellExecute);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_omits_inspect_when_debugging_is_off()
	{
		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", null!, launchWithDebugging: false, debuggingPort: 0, nodePath: "node");

		Assert.DoesNotContain("--inspect", info.Arguments);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_adds_a_bare_inspect_when_no_port_is_given()
	{
		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", null!, launchWithDebugging: true, debuggingPort: 0, nodePath: "node");

		Assert.StartsWith("--inspect ", info.Arguments);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_pins_the_inspector_port_when_one_is_given()
	{
		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", null!, launchWithDebugging: true, debuggingPort: 9229, nodePath: "node");

		Assert.StartsWith("--inspect=9229 ", info.Arguments);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_appends_the_project_to_NODE_PATH()
	{
		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", null!, launchWithDebugging: false, debuggingPort: 0, nodePath: "node");

		// Without this the child cannot resolve the project's node_modules.
		Assert.Contains(Path.Combine("/app", "node_modules"), info.Environment["NODE_PATH"]);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_copies_supplied_environment_variables()
	{
		var envVars = new Dictionary<string, string> { ["FOO"] = "bar" };

		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", envVars, launchWithDebugging: false, debuggingPort: 0, nodePath: "node");

		Assert.Equal("bar", info.Environment["FOO"]);
	}

	[Fact]
	public void BuildNodeProcessStartInfo_skips_a_null_environment_value()
	{
		var envVars = new Dictionary<string, string> { ["FOO"] = null! };

		var info = OutOfProcessNodeInstance.BuildNodeProcessStartInfo(
			"entry.js", "/app", "", envVars, launchWithDebugging: false, debuggingPort: 0, nodePath: "node");

		Assert.False(info.Environment.ContainsKey("FOO") && info.Environment["FOO"] is not null);
	}
}
