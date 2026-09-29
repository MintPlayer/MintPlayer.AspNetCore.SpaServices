using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// <see cref="AngularPrerendererBuilder.Build"/> end to end, with the build script replayed by a
/// <see cref="ScriptedLauncher"/> instead of started by npm.
/// </summary>
public class AngularPrerendererBuilderLauncherTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Build_completes_after_two_Build_at_lines()
    {
        // The default builder waits for the second "Build at:" - the first is the browser bundle.
        var launcher = new ScriptedLauncher("Build at: one\ncompiling\nBuild at: two\n", string.Empty) { ChildHasExited = false };
        var builder = new AngularPrerendererBuilder("build:ssr") { ProcessLauncher = launcher };

        await builder.Build(CreateSpaBuilder()).WaitAsync(Deadline);

        Assert.Contains("run build:ssr", launcher.LastStartInfo!.Arguments);
        Assert.Contains("--watch", launcher.LastStartInfo.Arguments);
        // A successful build leaves the --watch process running: it is what rebuilds the bundle.
        Assert.False(launcher.LastChild!.Killed);
    }

    [Fact]
    public async Task Build_fails_with_output_when_script_exits_without_success()
    {
        var launcher = new ScriptedLauncher("Build at: one\nerror TS2304: Cannot find name 'x'.\n", "npm ERR! code 1\n");
        var builder = new AngularPrerendererBuilder("build:ssr") { ProcessLauncher = launcher };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.Build(CreateSpaBuilder()).WaitAsync(Deadline));

        // Only one "Build at:" of the two arrived before the script ended. The captured output is
        // not asserted: a scripted child emits it all at once, before Build has attached its
        // output readers, so whether it is included is a race that a real build never loses.
        Assert.Contains("exited without indicating success", ex.Message);
        Assert.Contains("build:ssr", ex.Message);
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public async Task Build_stops_the_script_when_the_build_fails()
    {
        // A failed build is final, so its --watch process is of no further use. It used to keep
        // running until the host stopped.
        var launcher = new ScriptedLauncher("error TS2304: Cannot find name 'x'.\n", string.Empty) { ChildHasExited = false };
        var builder = new AngularPrerendererBuilder("build:ssr") { ProcessLauncher = launcher };

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.Build(CreateSpaBuilder()).WaitAsync(Deadline));

        Assert.True(launcher.LastChild!.Killed);
        Assert.True(launcher.LastChild.Disposed);
    }

    private static PrerenderingHarness.HarnessSpaBuilder CreateSpaBuilder()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(new PrerenderingHarness.HarnessApplicationLifetime())
            .AddSingleton<DiagnosticSource>(new DiagnosticListener("test"))
            .BuildServiceProvider();

        return new PrerenderingHarness.HarnessSpaBuilder(
            new ApplicationBuilder(services),
            new Core.SpaOptions { SourcePath = "C:/app", StartupTimeout = Deadline });
    }
}
