using Microsoft.AspNetCore.SpaServices;
using Microsoft.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Internals;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

public class AngularPrerendererBuilder : ISpaPrerendererBuilder
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
	public async Task Build(ISpaBuilder spaBuilder)
	{
		var pkgManagerCommand = spaBuilder.Options.PackageManagerCommand;
		var sourcePath = spaBuilder.Options.SourcePath;
		if (string.IsNullOrEmpty(sourcePath))
		{
			throw new InvalidOperationException($"To use {nameof(AngularPrerendererBuilder)}, you must supply a non-empty value for the {nameof(SpaOptions.SourcePath)} property of {nameof(SpaOptions)} when calling {nameof(SpaApplicationBuilderExtensions.UseSpa)}.");
		}

		var appBuilder = spaBuilder.ApplicationBuilder;
		var applicationStoppingToken = appBuilder.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
		var logger = LoggerFinder.GetOrCreateLogger(appBuilder, nameof(AngularPrerendererBuilder));
		var diagnosticSource = appBuilder.ApplicationServices.GetRequiredService<DiagnosticSource>();
		var scriptRunner = new NodeScriptRunner(
			sourcePath,
			npmScript,
			"--watch",
			null,
			pkgManagerCommand,
			diagnosticSource,
			applicationStoppingToken);
		scriptRunner.AttachToLogger(logger);

		using (var stdOutReader = new EventedStreamStringReader(scriptRunner.StdOut))
		using (var stdErrReader = new EventedStreamStringReader(scriptRunner.StdErr))
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
