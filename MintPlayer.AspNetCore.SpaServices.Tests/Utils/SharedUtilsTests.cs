using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.SpaServices.Utils;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Utils;

/// <summary>
/// The two small helpers that every process-spawning path in the package leans on. Both are now
/// shared by SpaServices and Prerendering rather than duplicated, so covering them once covers both.
/// </summary>
public class SharedUtilsTests
{
	[Fact]
	public void FindAvailablePort_returns_a_usable_ephemeral_port()
	{
		var port = TcpPortFinder.FindAvailablePort();

		// Port 0 means "pick one for me" - returning it would mean the listener was read before it
		// bound, which is the bug this guards.
		Assert.InRange(port, 1, 65535);
	}

	[Fact]
	public void FindAvailablePort_releases_the_port_it_reports()
	{
		var port = TcpPortFinder.FindAvailablePort();

		// The whole point of the helper is to hand back a port the CALLER can bind. If Stop() did not
		// release it, this second bind would throw SocketException.
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
		listener.Start();
		try
		{
			Assert.Equal(port, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
		}
		finally
		{
			listener.Stop();
		}
	}

	[Fact]
	public void GetOrCreateLogger_uses_the_factory_from_DI_when_there_is_one()
	{
		var provider = new ServiceCollection()
			.AddLogging(b => b.AddProvider(new CapturingLoggerProvider()))
			.BuildServiceProvider();
		var appBuilder = new ApplicationBuilder(provider);

		var logger = LoggerFinder.GetOrCreateLogger(appBuilder, "MyCategory");

		Assert.NotNull(logger);
		Assert.NotSame(NullLogger.Instance, logger);
	}

	[Fact]
	public void GetOrCreateLogger_falls_back_to_the_null_logger_when_DI_has_no_factory()
	{
		// An empty container - no AddLogging - is the case the null-logger fallback exists for.
		var provider = new ServiceCollection().BuildServiceProvider();
		var appBuilder = new ApplicationBuilder(provider);

		var logger = LoggerFinder.GetOrCreateLogger(appBuilder, "MyCategory");

		Assert.Same(NullLogger.Instance, logger);
	}

	[Fact]
	public void GetOrCreateLogger_passes_the_category_name_through()
	{
		var capturing = new CapturingLoggerProvider();
		var provider = new ServiceCollection()
			.AddLogging(b => b.AddProvider(capturing))
			.BuildServiceProvider();
		var appBuilder = new ApplicationBuilder(provider);

		LoggerFinder.GetOrCreateLogger(appBuilder, "A.Specific.Category");

		Assert.Contains("A.Specific.Category", capturing.Categories);
	}

	private sealed class CapturingLoggerProvider : ILoggerProvider
	{
		public List<string> Categories { get; } = [];

		public ILogger CreateLogger(string categoryName)
		{
			Categories.Add(categoryName);
			return NullLogger.Instance;
		}

		public void Dispose() { }
	}
}
