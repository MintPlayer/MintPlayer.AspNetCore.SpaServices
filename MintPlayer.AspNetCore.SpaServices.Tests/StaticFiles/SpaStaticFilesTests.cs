using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Core;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Internal;
using MintPlayer.AspNetCore.SpaServices.StaticFiles;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.StaticFiles;

public class SpaStaticFilesTests
{
    #region AddSpaStaticFilesImproved

    [Fact]
    public void Registers_the_static_file_provider_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddSpaStaticFilesImproved(options => options.RootPath = "dist");

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISpaStaticFileProvider));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Defers_the_configure_callback_until_the_provider_is_resolved()
    {
        // The callback runs inside the singleton factory, not at registration time. That matters:
        // a caller that only registers services never sees a RootPath validation failure.
        var invoked = false;
        var provider = BuildProviderWith(_ => invoked = true, rootPath: "dist");

        Assert.False(invoked);
        provider.GetService<ISpaStaticFileProvider>();
        Assert.True(invoked);
    }

    [Fact]
    public void Applies_the_configure_callback_on_top_of_the_options_configured_in_DI()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "from-callback"));

        var provider = BuildProviderWith(
            options => options.RootPath = "from-callback",
            rootPath: "from-di",
            contentRootPath: root.Path);

        var spaStaticFiles = provider.GetRequiredService<ISpaStaticFileProvider>();

        // The directory only exists under the callback's RootPath, so a non-null file provider
        // proves the callback won over the DI-configured value.
        Assert.NotNull(spaStaticFiles.FileProvider);
    }

    [Fact]
    public void Rejects_an_empty_root_path_when_the_provider_is_resolved()
    {
        var provider = BuildProviderWith(configuration: null, rootPath: string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpaStaticFileProvider>());
        Assert.Contains(nameof(SpaStaticFilesOptions.RootPath), ex.Message);
    }

    #endregion

    #region DefaultSpaStaticFileProvider

    [Fact]
    public void Serves_files_from_the_root_path_resolved_against_the_content_root()
    {
        using var root = new TempDirectory();
        var distPath = Path.Combine(root.Path, "dist");
        Directory.CreateDirectory(distPath);
        File.WriteAllText(Path.Combine(distPath, "index.html"), "<html></html>");

        var sut = new DefaultSpaStaticFileProvider(
            serviceProvider: ServicesWithEnvironment(root.Path),
            options: new SpaStaticFilesOptions { RootPath = "dist" });

        Assert.NotNull(sut.FileProvider);
        Assert.True(sut.FileProvider!.GetFileInfo("index.html").Exists);
    }

    [Fact]
    public void Supplies_no_file_provider_when_the_root_path_does_not_exist()
    {
        // A missing directory is the normal development case (files come from the dev server),
        // so it must not be an error - it just means nothing is served.
        using var root = new TempDirectory();

        var sut = new DefaultSpaStaticFileProvider(
            serviceProvider: ServicesWithEnvironment(root.Path),
            options: new SpaStaticFilesOptions { RootPath = "never-built" });

        Assert.Null(sut.FileProvider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_an_empty_root_path_on_construction(string? rootPath)
    {
        // Note the inconsistency with AddSpaStaticFilesImproved, which throws InvalidOperationException
        // for the same condition. Pinned as current behaviour.
        using var root = new TempDirectory();

        Assert.Throws<ArgumentException>(() => new DefaultSpaStaticFileProvider(
            serviceProvider: ServicesWithEnvironment(root.Path),
            options: new SpaStaticFilesOptions { RootPath = rootPath! }));
    }

    #endregion

    #region UseSpaStaticFiles

    [Fact]
    public void UseSpaStaticFiles_rejects_a_null_application_builder()
        => Assert.Throws<ArgumentNullException>(() => ((IApplicationBuilder)null!).UseSpaStaticFiles(new StaticFileOptions()));

    [Fact]
    public void UseSpaStaticFiles_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => NewApplicationBuilder().UseSpaStaticFiles(null!));

    [Fact]
    public void Adopts_the_file_provider_supplied_by_the_registered_service()
    {
        var fileProvider = new NullFileProvider();
        var app = new CountingApplicationBuilder(NewApplicationBuilder(new StubSpaStaticFileProvider(fileProvider)));
        var options = new StaticFileOptions();

        app.UseSpaStaticFiles(options);

        Assert.Same(fileProvider, options.FileProvider);
        Assert.Equal(1, app.UseCount);
    }

    [Fact]
    public void Registers_no_middleware_when_the_service_supplies_no_file_provider()
    {
        var app = new CountingApplicationBuilder(NewApplicationBuilder(new StubSpaStaticFileProvider(null)));

        app.UseSpaStaticFiles(new StaticFileOptions());

        Assert.Equal(0, app.UseCount);
    }

    [Fact]
    public void Keeps_an_explicitly_supplied_file_provider_without_consulting_DI()
    {
        // An explicit FileProvider short-circuits the whole lookup, so this does NOT throw even
        // though no ISpaStaticFileProvider is registered. That is what lets several UseSpa calls
        // each serve their own directory.
        var fileProvider = new NullFileProvider();
        var app = new CountingApplicationBuilder(NewApplicationBuilder());
        var options = new StaticFileOptions { FileProvider = fileProvider };

        app.UseSpaStaticFiles(options);

        Assert.Same(fileProvider, options.FileProvider);
        Assert.Equal(1, app.UseCount);
    }

    [Fact]
    public void Requires_a_registered_service_before_serving_spa_static_files()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewApplicationBuilder().UseSpaStaticFilesImproved());

        Assert.Contains(nameof(SpaStaticFilesExtensions.AddSpaStaticFilesImproved), ex.Message);
    }

    [Fact]
    public void Falls_back_on_the_web_root_when_the_caller_allows_it()
    {
        // The fallback leaves FileProvider null so that UseStaticFiles resolves the web root itself.
        var app = new CountingApplicationBuilder(NewApplicationBuilder());
        var options = new StaticFileOptions();

        app.UseSpaStaticFilesInternal(options, allowFallbackOnServingWebRootFiles: true);

        Assert.Null(options.FileProvider);
        Assert.Equal(1, app.UseCount);
    }

    [Fact]
    public void UseSpaStaticFilesInternal_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => NewApplicationBuilder().UseSpaStaticFilesInternal(null!, false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_registered_service_wins_over_the_web_root_fallback(bool allowFallback)
    {
        // Whether the fallback is allowed is irrelevant once a service is registered: its answer
        // (including a null file provider, meaning "serve nothing") is final.
        var decision = InvokeShouldServeStaticFiles(
            NewApplicationBuilder(new StubSpaStaticFileProvider(null)),
            allowFallback,
            out var fileProvider);

        Assert.False(decision);
        Assert.Null(fileProvider);
    }

    #endregion

    #region DefaultSpaBuilder

    [Fact]
    public void Spa_builder_exposes_the_application_builder_and_options_it_was_given()
    {
        var app = NewApplicationBuilder();
        var options = new SpaOptions();

        var sut = new DefaultSpaBuilder(app, options);

        Assert.Same(app, sut.ApplicationBuilder);
        Assert.Same(options, sut.Options);
    }

    [Fact]
    public void Spa_builder_rejects_a_null_application_builder()
        => Assert.Throws<ArgumentNullException>(() => new DefaultSpaBuilder(null!, new SpaOptions()));

    [Fact]
    public void Spa_builder_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => new DefaultSpaBuilder(NewApplicationBuilder(), null!));

    #endregion

    #region UseSpaImproved

    [Fact]
    public void UseSpaImproved_rejects_a_null_configuration_callback()
        => Assert.Throws<ArgumentNullException>(() => NewApplicationBuilder().UseSpaImproved(null!));

    [Fact]
    public void Invokes_the_configuration_callback_with_a_builder_over_the_same_application()
    {
        var app = NewApplicationBuilder(new StubSpaStaticFileProvider(null));
        Abstractions.ISpaBuilder? captured = null;

        app.UseSpaImproved(builder => captured = builder);

        Assert.NotNull(captured);
        Assert.Same(app, captured!.ApplicationBuilder);
    }

    [Fact]
    public void Hands_the_callback_a_clone_rather_than_the_options_registered_in_DI()
    {
        var app = NewApplicationBuilder(new StubSpaStaticFileProvider(null), services
            => services.Configure<SpaOptions>(o => o.DefaultPage = "/from-di.html"));
        var registered = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpaOptions>>().Value;

        app.UseSpaImproved(builder =>
        {
            Assert.NotSame(registered, builder.Options);
            builder.Options.DefaultPage = "/mutated.html";
        });

        Assert.Equal("/from-di.html", registered.DefaultPage.Value);
    }

    [Fact]
    public void Isolates_successive_UseSpa_calls_from_one_another()
    {
        var app = NewApplicationBuilder(new StubSpaStaticFileProvider(null), services
            => services.Configure<SpaOptions>(o => o.DefaultPage = "/from-di.html"));

        app.UseSpaImproved(builder => builder.Options.DefaultPage = "/first.html");

        string? secondSaw = null;
        app.UseSpaImproved(builder => secondSaw = builder.Options.DefaultPage.Value);

        Assert.Equal("/from-di.html", secondSaw);
    }

    #endregion

    #region SpaDefaultPageMiddleware

    [Fact]
    public void Attach_rejects_a_null_spa_builder()
        => Assert.Throws<ArgumentNullException>(() => SpaDefaultPageMiddleware.Attach(null!));

    [Fact]
    public async Task Rewrites_every_request_to_the_default_page()
    {
        var (context, _) = await RunDefaultPagePipeline(
            configure: options => options.DefaultPage = "/app.html",
            requestPath: "/some/deep/route");

        Assert.Equal("/app.html", context.Request.Path.Value);
    }

    [Fact]
    public async Task Reports_a_missing_default_page_as_an_unusable_SPA()
    {
        var (_, error) = await RunDefaultPagePipeline(
            configure: options => options.DefaultPage = "/app.html");

        var ex = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("'/app.html'", ex.Message);
        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public async Task Points_at_publishing_when_the_default_page_is_missing_in_production()
    {
        var (_, error) = await RunDefaultPagePipeline(environmentName: Environments.Production);

        Assert.Contains("running in Production mode", Assert.IsType<InvalidOperationException>(error).Message);
    }

    [Fact]
    public async Task Omits_the_publishing_hint_outside_production()
    {
        var (_, error) = await RunDefaultPagePipeline(environmentName: Environments.Development);

        Assert.DoesNotContain("running in Production mode", Assert.IsType<InvalidOperationException>(error).Message);
    }

    [Fact]
    public async Task Omits_the_publishing_hint_when_no_hosting_environment_is_available()
    {
        var (_, error) = await RunDefaultPagePipeline(environmentName: null);

        Assert.DoesNotContain("running in Production mode", Assert.IsType<InvalidOperationException>(error).Message);
    }

    [Fact]
    public async Task Leaves_a_request_that_already_matched_an_endpoint_alone()
    {
        // A deferred endpoint match means routing owns the request, so neither the rewrite nor the
        // "SPA is broken" exception should fire.
        var (context, error) = await RunDefaultPagePipeline(
            configure: options => options.DefaultPage = "/app.html",
            requestPath: "/api/values",
            endpoint: new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "test"));

        // The pipeline's terminal complains that the endpoint was never executed - that comes from
        // ApplicationBuilder, not from the SPA middleware, and proves the SPA middleware stood down.
        Assert.DoesNotContain("SPA default page middleware", error?.Message ?? string.Empty);
        Assert.Equal("/api/values", context.Request.Path.Value);
    }

    #endregion

    #region Helpers

    private static async Task<(DefaultHttpContext Context, Exception? Error)> RunDefaultPagePipeline(
        Action<SpaOptions>? configure = null,
        string requestPath = "/",
        string? environmentName = "Development",
        Endpoint? endpoint = null)
    {
        // A stub provider that supplies no file provider keeps the static-file middleware out of the
        // pipeline entirely, which is exactly the "SPA was never built" situation under test.
        var services = new ServiceCollection();
        services.AddSingleton<ISpaStaticFileProvider>(new StubSpaStaticFileProvider(null));
        if (environmentName != null)
        {
            services.AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment(Path.GetTempPath(), environmentName));
        }

        var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        var options = new SpaOptions();
        configure?.Invoke(options);
        SpaDefaultPageMiddleware.Attach(new DefaultSpaBuilder(app, options));

        var pipeline = app.Build();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = requestPath;
        if (endpoint != null)
        {
            context.SetEndpoint(endpoint);
        }

        // The pipeline's last middleware throws by design, so the exception is returned rather than
        // propagated - every test here needs to inspect the context afterwards either way.
        Exception? error = null;
        try
        {
            await pipeline(context);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (context, error);
    }

    private static bool InvokeShouldServeStaticFiles(IApplicationBuilder app, bool allowFallback, out IFileProvider? fileProvider)
    {
        var method = typeof(SpaStaticFilesExtensions).GetMethod(
            "ShouldServeStaticFiles",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var args = new object?[] { app, allowFallback, null };
        var result = (bool)method.Invoke(null, args)!;
        fileProvider = (IFileProvider?)args[2];
        return result;
    }

    private static ServiceProvider BuildProviderWith(
        Action<SpaStaticFilesOptions>? configuration,
        string rootPath,
        string? contentRootPath = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment(
            contentRootPath ?? Path.GetTempPath(),
            Environments.Development));
        services.Configure<SpaStaticFilesOptions>(o => o.RootPath = rootPath);
        services.AddSpaStaticFilesImproved(configuration);
        return services.BuildServiceProvider();
    }

    private static IServiceProvider ServicesWithEnvironment(string contentRootPath)
        => new ServiceCollection()
            .AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment(contentRootPath, Environments.Development))
            .BuildServiceProvider();

    private static IApplicationBuilder NewApplicationBuilder(
        ISpaStaticFileProvider? spaStaticFileProvider = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        if (spaStaticFileProvider != null)
        {
            services.AddSingleton(spaStaticFileProvider);
        }

        configureServices?.Invoke(services);
        return new ApplicationBuilder(services.BuildServiceProvider());
    }

    /// <summary>
    /// Counts middleware registrations so a test can tell "registered the static-file middleware"
    /// apart from "returned without touching the pipeline" - <see cref="IApplicationBuilder"/>
    /// exposes no way to inspect what has been added.
    /// </summary>
    private sealed class CountingApplicationBuilder(IApplicationBuilder inner) : IApplicationBuilder
    {
        public int UseCount { get; private set; }

        public IServiceProvider ApplicationServices
        {
            get => inner.ApplicationServices;
            set => inner.ApplicationServices = value;
        }

        public IFeatureCollection ServerFeatures => inner.ServerFeatures;

        public IDictionary<string, object?> Properties => inner.Properties;

        public RequestDelegate Build() => inner.Build();

        public IApplicationBuilder New() => inner.New();

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            UseCount++;
            inner.Use(middleware);
            return this;
        }
    }

    private sealed class StubSpaStaticFileProvider(IFileProvider? fileProvider) : ISpaStaticFileProvider
    {
        public IFileProvider? FileProvider => fileProvider;
    }

    private sealed class StubWebHostEnvironment(string contentRootPath, string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = environmentName;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "spa-static-files-tests-" + Guid.NewGuid().ToString("n"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A PhysicalFileProvider may still hold a watcher handle; leaving a stray temp
                // directory behind is preferable to failing an otherwise passing test.
            }
        }
    }

    #endregion
}
