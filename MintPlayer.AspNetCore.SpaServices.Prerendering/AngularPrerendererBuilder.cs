using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

public class AngularPrerendererBuilder : Abstractions.ISpaPrerendererBuilder
{
	private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5); // This is a development-time only feature, so a very long timeout is fine

	private readonly string npmScript;
	private readonly Regex finishedRegex;
	private readonly int finishedRegexIndex;

	/// <summary>
	/// Constructs an instance of <see cref="AngularPrerendererBuilder"/>.
	/// </summary>
	/// <param name="npmScript">The name of the script in your package.json file that builds the server-side bundle for your Angular application.</param>
	public AngularPrerendererBuilder(string npmScript) : this(npmScript, @"Build at\:", 2) { }
	//public AngularPrerendererBuilder(string npmScript) : this(npmScript, "Entrypoint main", 1) { }

	/// <summary>
	/// Constructs an instance of <see cref="AngularPrerendererBuilder"/>.
	/// </summary>
	/// <param name="npmScript">The name of the script in your package.json file that builds the server-side bundle for your Angular application.</param>
	/// <param name="finishedRegex">Regular expression which indicates that the build command completed.</param>
	/// <param name="finishedRegexNumber">Occurrance of the <see cref="finishedRegex"/> (index).</param>
	public AngularPrerendererBuilder(string npmScript, string finishedRegex, int finishedRegexNumber)
	{
		if (string.IsNullOrEmpty(npmScript))
		{
			throw new ArgumentException("Cannot be null or empty.", nameof(npmScript));
		}

		this.npmScript = npmScript;
		//this.finishedRegex = new Regex(finishedRegex ?? "Entrypoint main", RegexOptions.None, RegexMatchTimeout);
		this.finishedRegex = new Regex(finishedRegex ?? @"Build at\:", RegexOptions.None, RegexMatchTimeout);
		this.finishedRegexIndex = finishedRegexNumber;
	}

	/// <inheritdoc />
	public async Task Build(Abstractions.ISpaBuilder spaBuilder)
	{
		var pkgManagerCommand = spaBuilder.Options.PackageManagerCommand;
		var sourcePath = spaBuilder.Options.SourcePath;
		if (string.IsNullOrEmpty(sourcePath))
		{
			throw new InvalidOperationException($"To use {nameof(AngularPrerendererBuilder)}, you must supply a non-empty value for the {nameof(Core.SpaOptions.SourcePath)} property of {nameof(Core.SpaOptions)} when calling {nameof(MintPlayer.AspNetCore.SpaServices.Extensions.SpaApplicationBuilderExtensions.UseSpaImproved)}.");
		}

		var appBuilder = spaBuilder.ApplicationBuilder;
		var applicationStoppingToken = appBuilder.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
		var logger = Internals.LoggerFinder.GetOrCreateLogger(appBuilder, nameof(AngularPrerendererBuilder));
		var diagnosticSource = appBuilder.ApplicationServices.GetRequiredService<DiagnosticSource>();
		var scriptRunner = new Internals.NodeScriptRunner(
			sourcePath,
			npmScript,
			"--watch",
			null,
			pkgManagerCommand,
			diagnosticSource,
			applicationStoppingToken);
		scriptRunner.AttachToLogger(logger);

		using (var stdOutReader = new Internals.EventedStreamStringReader(scriptRunner.StdOut))
		using (var stdErrReader = new Internals.EventedStreamStringReader(scriptRunner.StdErr))
		{
			await WaitForBuildToFinish(
				scriptRunner.StdOut,
				finishedRegex,
				finishedRegexIndex,
				spaBuilder.Options.StartupTimeout,
				applicationStoppingToken,
				pkgManagerCommand,
				npmScript,
				stdOutReader,
				stdErrReader);
		}
	}

	/// <summary>
	/// Waits until the build script has reported success <paramref name="occurrences"/> times, or
	/// fails with the script's own output attached.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Uses <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> rather than the
	/// <c>WithTimeout</c> helper because it propagates the inner fault unwrapped - the helper
	/// surfaces it as an <see cref="AggregateException"/>, which would stop
	/// <see cref="EndOfStreamException"/> from being caught below and lose the npm output along with
	/// it. It also distinguishes the two failure modes by type on its own: a timeout throws
	/// <see cref="TimeoutException"/> while shutdown throws <see cref="OperationCanceledException"/>,
	/// whereas a single linked token would report a Ctrl+C as "the build timed out".
	/// </para>
	/// <para>
	/// Before this, nothing bounded the wait at all: a build script that neither matched nor exited
	/// left the first request hanging forever, and <see cref="Core.SpaOptions.StartupTimeout"/> was
	/// read by the prerendering middleware and then never used.
	/// </para>
	/// </remarks>
	internal static async Task WaitForBuildToFinish(
		Internals.EventedStreamReader stdOut,
		Regex finishedRegex,
		int occurrences,
		TimeSpan timeout,
		CancellationToken applicationStoppingToken,
		string pkgManagerCommand,
		string npmScript,
		Internals.EventedStreamStringReader stdOutReader,
		Internals.EventedStreamStringReader stdErrReader)
	{
		try
		{
			for (var i = 0; i < occurrences; i++)
			{
				await stdOut.WaitForMatch(finishedRegex).WaitAsync(timeout, applicationStoppingToken);
			}
		}
		catch (EndOfStreamException ex)
		{
			throw new InvalidOperationException(
				$"The {pkgManagerCommand} script '{npmScript}' exited without indicating success.\n" +
				$"Output was: {stdOutReader.ReadAsString()}\n" +
				$"Error output was: {stdErrReader.ReadAsString()}", ex);
		}
		catch (TimeoutException ex)
		{
			throw new InvalidOperationException(
				$"The {pkgManagerCommand} script '{npmScript}' did not indicate success within the " +
				$"timeout period of {timeout.TotalSeconds} seconds. Adjust " +
				$"{nameof(Core.SpaOptions)}.{nameof(Core.SpaOptions.StartupTimeout)} if the build " +
				$"legitimately takes longer.\n" +
				$"Output was: {stdOutReader.ReadAsString()}\n" +
				$"Error output was: {stdErrReader.ReadAsString()}", ex);
		}
		catch (OperationCanceledException ex)
		{
			throw new InvalidOperationException(
				$"The {pkgManagerCommand} script '{npmScript}' was still running when the application " +
				$"began shutting down.\n" +
				$"Output was: {stdOutReader.ReadAsString()}\n" +
				$"Error output was: {stdErrReader.ReadAsString()}", ex);
		}
	}
}
