using MintPlayer.Dotnet.JobObjects;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

public class AngularPrerendererBuilder : Abstractions.ISpaPrerendererBuilder
{
	private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5); // This is a development-time only feature, so a very long timeout is fine

	private readonly string npmScript;
	private readonly Regex finishedRegex;
	private readonly int finishedRegexIndex;
	private readonly ChildProcessManager mgr;

	/// <summary>
	/// Constructs an instance of <see cref="AngularPrerendererBuilder"/>.
	/// </summary>
	/// <param name="npmScript">The name of the script in your package.json file that builds the server-side bundle for your Angular application.</param>
	public AngularPrerendererBuilder(string npmScript, MintPlayer.Dotnet.JobObjects.ChildProcessManager mgr) : this(npmScript, @"Build at\:", 2, mgr) { }
	//public AngularPrerendererBuilder(string npmScript) : this(npmScript, "Entrypoint main", 1) { }

	/// <summary>
	/// Constructs an instance of <see cref="AngularPrerendererBuilder"/>.
	/// </summary>
	/// <param name="npmScript">The name of the script in your package.json file that builds the server-side bundle for your Angular application.</param>
	/// <param name="finishedRegex">Regular expression which indicates that the build command completed.</param>
	/// <param name="finishedRegexNumber">Occurrance of the <see cref="finishedRegex"/> (index).</param>
	public AngularPrerendererBuilder(string npmScript, string finishedRegex, int finishedRegexNumber, MintPlayer.Dotnet.JobObjects.ChildProcessManager mgr)
	{
		if (string.IsNullOrEmpty(npmScript))
		{
			throw new ArgumentException("Cannot be null or empty.", nameof(npmScript));
		}

		this.npmScript = npmScript;
		//this.finishedRegex = new Regex(finishedRegex ?? "Entrypoint main", RegexOptions.None, RegexMatchTimeout);
		this.finishedRegex = new Regex(finishedRegex ?? @"Build at\:", RegexOptions.None, RegexMatchTimeout);
		this.finishedRegexIndex = finishedRegexNumber;
		this.mgr = mgr;
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
			applicationStoppingToken, mgr);
		scriptRunner.AttachToLogger(logger);

		using (var stdOutReader = new Internals.EventedStreamStringReader(scriptRunner.StdOut))
		using (var stdErrReader = new Internals.EventedStreamStringReader(scriptRunner.StdErr))
		{
			try
			{
				for (var i = 0; i <finishedRegexIndex; i++)
				{
					await scriptRunner.StdOut.WaitForMatch(finishedRegex);
				}
			}
			catch (EndOfStreamException ex)
			{
				throw new InvalidOperationException(
					$"The {pkgManagerCommand} script '{npmScript}' exited without indicating success.\n" +
					$"Output was: {stdOutReader.ReadAsString()}\n" +
					$"Error output was: {stdErrReader.ReadAsString()}", ex);
			}
			catch (OperationCanceledException ex)
			{
				throw new InvalidOperationException(
					$"The {pkgManagerCommand} script '{npmScript}' timed out without indicating success. " +
					$"Output was: {stdOutReader.ReadAsString()}\n" +
					$"Error output was: {stdErrReader.ReadAsString()}", ex);
			}
		}
	}
}
