using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// Covers the parts of NodeServices that can be exercised without a Node.js process. Nothing here
/// may call <see cref="NodeServicesOptions.NodeInstanceFactory"/> - invoking it launches node.
/// </summary>
public class NodeServicesUtilTests
{
    #region NodeServicesOptions

    [Fact]
    public void Options_reject_a_null_service_provider()
        => Assert.Throws<ArgumentNullException>(() => new NodeServicesOptions(null!));

    [Fact]
    public void Options_default_to_a_one_minute_invocation_timeout_and_the_node_on_PATH()
    {
        var options = new NodeServicesOptions(EmptyServices());

        Assert.Equal(60 * 1000, options.InvocationTimeoutMilliseconds);
        Assert.Equal("node", options.NodePath);
        Assert.False(options.LaunchWithDebugging);
        Assert.Equal(0, options.DebuggingPort);
    }

    [Fact]
    public void Options_watch_the_source_extensions_a_javascript_project_uses()
    {
        var options = new NodeServicesOptions(EmptyServices());

        Assert.Equal(new[] { ".js", ".jsx", ".ts", ".tsx", ".json", ".html" }, options.WatchFileExtensions);
    }

    [Fact]
    public void Options_hand_out_their_own_copy_of_the_watched_extensions()
    {
        // The defaults live in a static array, so without the defensive clone one caller's edit
        // would silently change what every later NodeServicesOptions watches.
        var first = new NodeServicesOptions(EmptyServices());
        first.WatchFileExtensions[0] = ".mutated";

        var second = new NodeServicesOptions(EmptyServices());

        Assert.Equal(".js", second.WatchFileExtensions[0]);
    }

    [Fact]
    public void Options_fall_back_on_the_current_directory_outside_a_web_host()
    {
        var options = new NodeServicesOptions(EmptyServices());

        Assert.Equal(Directory.GetCurrentDirectory(), options.ProjectPath);
        Assert.Empty(options.EnvironmentVariables);
    }

    [Fact]
    public void Options_take_the_project_path_from_the_hosting_environment()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "node-services-options-tests");

        var options = new NodeServicesOptions(ServicesWithEnvironment(contentRoot, Environments.Development));

        Assert.Equal(contentRoot, options.ProjectPath);
    }

    [Theory]
    [InlineData("Development", "development")]
    [InlineData("Production", "production")]
    [InlineData("Staging", "production")]
    public void Options_translate_the_host_environment_into_NODE_ENV(string environmentName, string expectedNodeEnv)
    {
        // Anything that is not Development maps onto Node's "production", which is the de-facto
        // standard pair of values - Node has no notion of a staging environment.
        var options = new NodeServicesOptions(ServicesWithEnvironment(Path.GetTempPath(), environmentName));

        Assert.Equal(expectedNodeEnv, options.EnvironmentVariables["NODE_ENV"]);
    }

    [Fact]
    public void Options_log_node_output_nowhere_when_no_logger_factory_is_available()
    {
        var options = new NodeServicesOptions(EmptyServices());

        Assert.Same(NullLogger.Instance, options.NodeInstanceOutputLogger);
    }

    [Fact]
    public void Options_log_node_output_through_the_registered_logger_factory()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var options = new NodeServicesOptions(services);

        Assert.NotSame(NullLogger.Instance, options.NodeInstanceOutputLogger);
    }

    [Fact]
    public void Options_adopt_the_hosts_shutdown_token()
    {
        var lifetime = new StubApplicationLifetime();
        var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .BuildServiceProvider();

        var options = new NodeServicesOptions(services);

        Assert.Equal(lifetime.ApplicationStopping, options.ApplicationStoppingToken);
    }

    [Fact]
    public void Options_leave_the_shutdown_token_unset_outside_a_host()
    {
        var options = new NodeServicesOptions(EmptyServices());

        Assert.Equal(CancellationToken.None, options.ApplicationStoppingToken);
    }

    [Fact]
    public void Options_arrive_preconfigured_for_out_of_process_http_hosting()
    {
        // Only the presence of the factory is asserted - calling it would launch a node process.
        var options = new NodeServicesOptions(EmptyServices());

        Assert.NotNull(options.NodeInstanceFactory);
    }

    #endregion

    #region NodeServicesFactory / AddNodeServices

    [Fact]
    public void The_factory_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => NodeServicesFactory.CreateNodeServices(null!));

    [Fact]
    public void The_factory_builds_a_service_without_starting_node()
    {
        var factoryCalls = 0;
        var options = new NodeServicesOptions(EmptyServices())
        {
            NodeInstanceFactory = () => { factoryCalls++; return null!; },
        };

        var nodeServices = NodeServicesFactory.CreateNodeServices(options);

        Assert.NotNull(nodeServices);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void AddNodeServices_rejects_a_null_setup_action()
        => Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddNodeServices(null!));

    [Fact]
    public void AddNodeServices_registers_a_single_shared_INodeServices()
    {
        var services = new ServiceCollection();
        services.AddNodeServices();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<INodeServices>();
        Assert.Same(first, provider.GetRequiredService<INodeServices>());
    }

    [Fact]
    public void AddNodeServices_lets_the_caller_override_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddNodeServices(options =>
        {
            Assert.Equal(60 * 1000, options.InvocationTimeoutMilliseconds);
            options.InvocationTimeoutMilliseconds = 1234;
            options.NodePath = "custom-node";
        });

        using var provider = services.BuildServiceProvider();

        // The setup action only runs when the singleton is first resolved.
        Assert.NotNull(provider.GetRequiredService<INodeServices>());
    }

    #endregion

    #region NodeInvocationInfo

    [Fact]
    public void Invocation_info_carries_the_module_export_and_arguments()
    {
        var info = new NodeInvocationInfo
        {
            ModuleName = "./dist/render",
            ExportedFunctionName = "renderPage",
            Args = ["a", 1],
        };

        Assert.Equal("./dist/render", info.ModuleName);
        Assert.Equal("renderPage", info.ExportedFunctionName);
        Assert.Equal(new object[] { "a", 1 }, info.Args);
    }

    #endregion

    #region StringAsTempFile

    [Fact]
    public void Temp_file_holds_the_content_it_was_created_from()
    {
        using var sut = new StringAsTempFile("console.log('hi');", CancellationToken.None);

        Assert.True(File.Exists(sut.FileName));
        Assert.Equal("console.log('hi');", File.ReadAllText(sut.FileName));
    }

    [Fact]
    public void Temp_file_uses_a_js_extension_so_node_can_load_it_as_a_module()
    {
        using var sut = new StringAsTempFile("// empty", CancellationToken.None);

        Assert.Equal(".js", Path.GetExtension(sut.FileName));
    }

    [Fact]
    public void Temp_file_is_deleted_on_dispose()
    {
        var sut = new StringAsTempFile("// empty", CancellationToken.None);
        var fileName = sut.FileName;

        sut.Dispose();

        Assert.False(File.Exists(fileName));
    }

    [Fact]
    public void Temp_file_tolerates_being_disposed_twice()
    {
        var sut = new StringAsTempFile("// empty", CancellationToken.None);

        sut.Dispose();
        sut.Dispose();

        Assert.False(File.Exists(sut.FileName));
    }

    [Fact]
    public void Temp_file_is_deleted_when_the_application_stops()
    {
        // Finalizers do not reliably run at process shutdown, hence the token registration.
        using var cts = new CancellationTokenSource();
        using var sut = new StringAsTempFile("// empty", cts.Token);

        cts.Cancel();

        Assert.False(File.Exists(sut.FileName));
    }

    [Fact]
    public void Temp_file_deletion_happens_only_once()
    {
        // Disposing after the token already deleted the file must not try (and fail) to delete a
        // path that a later StringAsTempFile could by then have reused.
        using var cts = new CancellationTokenSource();
        var sut = new StringAsTempFile("// empty", cts.Token);
        cts.Cancel();

        File.WriteAllText(sut.FileName, "someone else's file");
        sut.Dispose();

        Assert.True(File.Exists(sut.FileName));
        File.Delete(sut.FileName);
    }

    #endregion

    #region TaskExtensions

    [Fact]
    public void A_completed_task_is_passed_straight_through()
    {
        // No continuation is allocated, so a cancelled token cannot turn an already-finished task
        // into a cancelled one.
        var task = Task.CompletedTask;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Same(task, task.OrThrowOnCancellation(cts.Token));
    }

    [Fact]
    public void A_completed_task_with_a_result_is_passed_straight_through()
    {
        var task = Task.FromResult(42);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Same(task, task.OrThrowOnCancellation(cts.Token));
    }

    [Fact]
    public async Task A_pending_task_is_cancelled_by_an_already_cancelled_token()
    {
        var source = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var wrapped = source.Task.OrThrowOnCancellation(cts.Token);

        await Assert.ThrowsAsync<TaskCanceledException>(() => wrapped);
        source.SetResult();
    }

    [Fact]
    public async Task A_pending_task_with_a_result_is_cancelled_by_an_already_cancelled_token()
    {
        var source = new TaskCompletionSource<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var wrapped = source.Task.OrThrowOnCancellation(cts.Token);

        await Assert.ThrowsAsync<TaskCanceledException>(() => wrapped);
        source.SetResult(1);
    }

    [Fact]
    public async Task A_pending_task_completes_normally_while_the_token_stays_live()
    {
        var source = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var wrapped = source.Task.OrThrowOnCancellation(cts.Token);
        Assert.NotSame(source.Task, wrapped);

        source.SetResult();
        await wrapped;
    }

    [Fact]
    public async Task A_pending_task_passes_its_result_through()
    {
        var source = new TaskCompletionSource<int>();
        using var cts = new CancellationTokenSource();

        var wrapped = source.Task.OrThrowOnCancellation(cts.Token);
        source.SetResult(42);

        Assert.Equal(42, await wrapped);
    }

    #endregion

    #region EmbeddedResourceReader

    [Fact]
    public void Reads_an_embedded_resource_addressed_by_its_path()
    {
        // The reader turns "/Content/Node/entrypoint-http.js" into the manifest name
        // "<assembly>.Content.Node.entrypoint-http.js", so the leading slash is load-bearing.
        var content = EmbeddedResourceReader.Read(typeof(NodeServicesOptions), "/Content/Node/entrypoint-http.js");

        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void An_unknown_resource_surfaces_as_a_null_stream_rather_than_a_helpful_error()
    {
        // Current behaviour, and worth knowing: a missing resource yields a null stream that the
        // StreamReader constructor rejects, so the failure names "stream" instead of the resource.
        Assert.Throws<ArgumentNullException>(
            () => EmbeddedResourceReader.Read(typeof(NodeServicesOptions), "/Content/Node/does-not-exist.js"));
    }

    #endregion

    #region Helpers

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    private static IServiceProvider ServicesWithEnvironment(string contentRootPath, string environmentName)
        => new ServiceCollection()
            .AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment(contentRootPath, environmentName))
            .BuildServiceProvider();

    private sealed class StubWebHostEnvironment(string contentRootPath, string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = environmentName;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
    }

    private sealed class StubApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }

    #endregion
}
