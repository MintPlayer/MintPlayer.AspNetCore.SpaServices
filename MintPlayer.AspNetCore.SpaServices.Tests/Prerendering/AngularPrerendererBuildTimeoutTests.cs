using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Internals;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// Covers the bounded wait on the SSR bundle build. Nothing used to bound it: a build script that
/// neither printed the finished marker nor exited left the first request hanging forever, while
/// <see cref="Core.SpaOptions.StartupTimeout"/> was read by the middleware and then never used.
/// </summary>
/// <remarks>
/// No npm process is involved. <see cref="AngularPrerendererBuilder.WaitForBuildToFinish"/> is
/// exercised directly over a <see cref="PrerenderingEventedStreamReaderTests.GatedStream"/>, whose
/// first read blocks until released - which is exactly the shape of a hung build.
/// </remarks>
public class AngularPrerendererBuildTimeoutTests
{
    private static readonly Regex FinishedRegex = new(@"Build at\:");

    /// <summary>Long enough that a passing test never waits on it, short enough to fail fast.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static async Task<Exception> RunExpectingFailure(
        string content,
        TimeSpan timeout,
        bool release,
        CancellationToken applicationStoppingToken = default)
    {
        using var stream = new PrerenderingEventedStreamReaderTests.GatedStream(content);
        var stdOut = new EventedStreamReader(new StreamReader(stream));
        using var stdOutReader = new EventedStreamStringReader(stdOut);
        using var stdErrStream = new PrerenderingEventedStreamReaderTests.GatedStream(string.Empty);
        var stdErr = new EventedStreamReader(new StreamReader(stdErrStream));
        using var stdErrReader = new EventedStreamStringReader(stdErr);

        if (release)
        {
            stream.Release();
        }

        stdErrStream.Release();

        var wait = AngularPrerendererBuilder.WaitForBuildToFinish(
            stdOut,
            FinishedRegex,
            occurrences: 1,
            timeout,
            applicationStoppingToken,
            "npm",
            "build:ssr",
            stdOutReader,
            stdErrReader);

        // A hard cap so a regression fails the test instead of hanging CI, which is what the
        // unbounded wait used to do to the first request.
        return await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Completes_when_the_build_reports_success()
    {
        using var stream = new PrerenderingEventedStreamReaderTests.GatedStream("compiling\nBuild at: 2026-08-27\n");
        var stdOut = new EventedStreamReader(new StreamReader(stream));
        using var stdOutReader = new EventedStreamStringReader(stdOut);
        using var stdErrStream = new PrerenderingEventedStreamReaderTests.GatedStream(string.Empty);
        var stdErr = new EventedStreamReader(new StreamReader(stdErrStream));
        using var stdErrReader = new EventedStreamStringReader(stdErr);

        var wait = AngularPrerendererBuilder.WaitForBuildToFinish(
            stdOut,
            FinishedRegex,
            occurrences: 1,
            TestTimeout,
            CancellationToken.None,
            "npm",
            "build:ssr",
            stdOutReader,
            stdErrReader);

        stream.Release();
        stdErrStream.Release();

        await wait.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Fails_with_the_script_output_when_the_build_never_reports_success()
    {
        // The build script exits without printing the finished marker. This diagnostic - and the
        // npm output it carries - is what a naive WithTimeout wrapper would have destroyed, by
        // wrapping EndOfStreamException in an AggregateException so the catch no longer matched.
        var ex = await RunExpectingFailure("some warning\nand then it exited\n", TestTimeout, release: true);

        Assert.Contains("exited without indicating success", ex.Message);
        Assert.Contains("build:ssr", ex.Message);
        Assert.Contains("and then it exited", ex.Message);
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public async Task Fails_with_a_timeout_when_the_build_hangs()
    {
        // Never released, so the stream neither matches nor closes - a hung build. Before this the
        // wait had no bound at all and the request hung with it.
        var ex = await RunExpectingFailure("", TimeSpan.FromMilliseconds(50), release: false);

        Assert.Contains("did not indicate success within the timeout period", ex.Message);
        Assert.Contains(nameof(Core.SpaOptions.StartupTimeout), ex.Message);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Reports_a_shutdown_separately_from_a_timeout()
    {
        // A timeout and a host shutdown must not report the same thing: a single linked token would
        // have made Ctrl+C during a slow build read as "the build timed out". Task.WaitAsync keeps
        // them apart by type, so the two arms can say what actually happened.
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var ex = await RunExpectingFailure("", TestTimeout, release: false, stopping.Token);

        Assert.Contains("still running when the application began shutting down", ex.Message);
        Assert.IsType<OperationCanceledException>(ex.InnerException, exactMatch: false);
    }
}
