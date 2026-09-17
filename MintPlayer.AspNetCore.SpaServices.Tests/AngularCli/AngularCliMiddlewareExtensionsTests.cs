using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Abstractions;
using MintPlayer.AspNetCore.SpaServices.Core;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.AngularCli;

/// <summary>
/// The guards on <c>UseAngularCliServer</c>. Only the rejection paths are covered: past them the
/// method attaches the middleware, which launches the Angular CLI.
/// </summary>
public class AngularCliMiddlewareExtensionsTests
{
	[Fact]
	public void Rejects_a_null_builder()
	{
		Assert.Throws<ArgumentNullException>(() => ((ISpaBuilder)null!).UseAngularCliServer());
	}

	[Fact]
	public void Rejects_an_empty_source_path()
	{
		// Without a SourcePath there is no directory to run the CLI in. Failing here names the
		// property to set; failing later would surface as an opaque process-start error.
		var builder = CreateSpaBuilder(sourcePath: string.Empty);

		var ex = Assert.Throws<InvalidOperationException>(() => builder.UseAngularCliServer());

		Assert.Contains(nameof(SpaOptions.SourcePath), ex.Message);
		Assert.Contains("UseAngularCliServer", ex.Message);
	}

	/// <summary>
	/// A builder carrying nothing but the options, which is all the guards read. Going through
	/// <c>UseSpaImproved</c> would need the whole static-file and hosting-environment graph, none of
	/// which these two rejections touch.
	/// </summary>
	private static ISpaBuilder CreateSpaBuilder(string sourcePath)
	{
		var services = new ServiceCollection()
			.AddLogging()
			.AddOptions()
			.BuildServiceProvider();

		return new OptionsOnlySpaBuilder(new ApplicationBuilder(services), new SpaOptions { SourcePath = sourcePath });
	}

	private sealed class OptionsOnlySpaBuilder(IApplicationBuilder applicationBuilder, ISpaOptions options) : ISpaBuilder
	{
		public IApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

		public ISpaOptions Options { get; } = options;
	}
}
