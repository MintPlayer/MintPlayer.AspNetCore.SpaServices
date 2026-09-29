using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.SpaServices.Abstractions;
using MintPlayer.AspNetCore.SpaServices.AngularCli;
using MintPlayer.AspNetCore.SpaServices.Core;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Tests.Proxying;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.AngularCli;

/// <summary>
/// The wiring half of the Angular CLI middleware: <c>Attach</c> and <c>UseAngularCliServer</c> past
/// their guards. These go through the shipped process launcher, so the package manager is a name
/// that does not exist - no npm, no Angular, no node.
/// </summary>
/// <remarks>
/// That fails the start on every platform, only differently: on Linux the launch itself throws, and
/// on Windows <c>cmd /c</c> starts, prints "is not recognized" and exits. Both end up as a faulted
/// start task, which is what these tests observe.
/// </remarks>
public class AngularCliAttachTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task Attach_registers_a_proxy_whose_requests_surface_the_startup_failure()
	{
		var (spaBuilder, pkgManager, _) = CreateSpaBuilder();

		AngularCliMiddleware.Attach(spaBuilder);

		var ex = await InvokeExpectingFailure(spaBuilder);
		Assert.Contains(pkgManager, ex.Message);
	}

	[Fact]
	public async Task Logs_a_startup_failure_even_when_no_request_arrives()
	{
		// Nothing awaits the start task until a request comes in, so without an observer a dev server
		// that failed to start was never reported at all.
		var (spaBuilder, pkgManager, logs) = CreateSpaBuilder();

		AngularCliMiddleware.Attach(spaBuilder);

		var error = await WaitForStartupFailure(logs);
		Assert.IsType<InvalidOperationException>(error.Exception);
		Assert.Contains(pkgManager, error.Exception!.Message);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UseAngularCliServer_with_a_source_path_attaches_the_middleware(bool withCliRegexes)
	{
		var (spaBuilder, pkgManager, _) = CreateSpaBuilder(withCliRegexes
			? [new Regex("open your browser on (?<openbrowser>http\\S+)")]
			: null);

		spaBuilder.UseAngularCliServer("start");

		var ex = await InvokeExpectingFailure(spaBuilder);
		Assert.Contains(pkgManager, ex.Message);
	}

	private static async Task<InvalidOperationException> InvokeExpectingFailure(ISpaBuilder spaBuilder)
	{
		var pipeline = spaBuilder.ApplicationBuilder.Build();
		var context = PerformProxyRequestTests.CreateContext(path: "/main.js");

		return await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context).WaitAsync(Deadline));
	}

	/// <summary>
	/// Polls, with a deadline, for the start-failure entry. It is not necessarily the first error:
	/// on Windows the script's own stderr ("is not recognized") is logged at Error as well.
	/// </summary>
	private static async Task<LoggedError> WaitForStartupFailure(ErrorCollectingLoggerProvider logs)
	{
		using var timeout = new CancellationTokenSource(Deadline);
		while (true)
		{
			lock (logs.Errors)
			{
				var failure = logs.Errors.FirstOrDefault(e => e.Message.Contains("failed to start"));
				if (failure is not null)
					return failure;
			}

			if (timeout.IsCancellationRequested)
				throw new TimeoutException("No error was logged for the failed start.");

			await Task.Delay(10);
		}
	}

	private static (ISpaBuilder SpaBuilder, string PkgManager, ErrorCollectingLoggerProvider Logs) CreateSpaBuilder(Regex[]? cliRegexes = null)
	{
		var pkgManager = "no-such-pm-" + Guid.NewGuid().ToString("n");
		var logs = new ErrorCollectingLoggerProvider();

		var services = new ServiceCollection()
			.AddOptions()
			.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]))
			.AddSingleton<IHostApplicationLifetime>(new PrerenderingHarness.HarnessApplicationLifetime())
			.AddSingleton<DiagnosticSource>(new DiagnosticListener("test"))
			.BuildServiceProvider();

		var options = new SpaOptions
		{
			SourcePath = Path.GetTempPath(),
			PackageManagerCommand = pkgManager,
			StartupTimeout = Deadline,
			CliRegexes = cliRegexes,
		};

		return (new TestSpaBuilder(new ApplicationBuilder(services), options), pkgManager, logs);
	}

	private sealed class TestSpaBuilder(IApplicationBuilder applicationBuilder, ISpaOptions options) : ISpaBuilder
	{
		public IApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

		public ISpaOptions Options { get; } = options;
	}

	private sealed record LoggedError(string Message, Exception? Exception);

	/// <summary>Keeps only Error entries, with their exception - the one thing these tests assert on.</summary>
	private sealed class ErrorCollectingLoggerProvider : ILoggerProvider
	{
		public List<LoggedError> Errors { get; } = [];

		public ILogger CreateLogger(string categoryName) => new ErrorLogger(this);

		public void Dispose() { }

		private sealed class ErrorLogger(ErrorCollectingLoggerProvider provider) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				if (logLevel != LogLevel.Error)
					return;

				lock (provider.Errors)
				{
					provider.Errors.Add(new LoggedError(formatter(state, exception), exception));
				}
			}
		}
	}
}
