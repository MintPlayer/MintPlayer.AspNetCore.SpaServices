using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// Covers the retry / connection-draining state machine in <c>NodeServicesImpl</c>. Everything here
/// runs against a fake <see cref="INodeInstance"/>, so no node process is ever launched.
/// </summary>
public class NodeServicesImplTests
{
    private static NodeInvocationException Unavailable(bool allowConnectionDraining = false)
        => new("boom", "details", nodeInstanceUnavailable: true, allowConnectionDraining);

    [Fact]
    public async Task Returns_the_instance_result()
    {
        var factory = new CountingFactory(_ => new FakeNodeInstance(_ => Task.FromResult<object>("ok")));
        using var services = new NodeServicesImpl(factory.Create);

        Assert.Equal("ok", await services.InvokeAsync<string>("module"));
    }

    [Fact]
    public async Task Creates_the_node_instance_lazily_and_only_once()
    {
        var factory = new CountingFactory(_ => new FakeNodeInstance(_ => Task.FromResult<object>("ok")));
        using var services = new NodeServicesImpl(factory.Create);

        Assert.Equal(0, factory.CreateCount);

        for (var i = 0; i < 3; i++)
            await services.InvokeAsync<string>("module");

        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Passes_a_null_export_name_for_the_module_level_overload()
    {
        string? seenExport = "not-set";
        var factory = new CountingFactory(_ => new FakeNodeInstance(info =>
        {
            seenExport = info.ExportedFunctionName;
            return Task.FromResult<object>("ok");
        }));
        using var services = new NodeServicesImpl(factory.Create);

        await services.InvokeAsync<string>("module");

        Assert.Null(seenExport);
    }

    [Fact]
    public async Task Forwards_the_module_export_name_and_arguments()
    {
        NodeInvocationInfo? seen = null;
        var factory = new CountingFactory(_ => new FakeNodeInstance(info =>
        {
            seen = info;
            return Task.FromResult<object>("ok");
        }));
        using var services = new NodeServicesImpl(factory.Create);

        await services.InvokeExportAsync<string>("./module", "render", 1, "two");

        Assert.Equal("./module", seen!.ModuleName);
        Assert.Equal("render", seen.ExportedFunctionName);
        Assert.Equal([1, "two"], seen.Args);
    }

    [Fact]
    public async Task Retries_once_on_a_new_instance_when_the_node_instance_is_unavailable()
    {
        var factory = new CountingFactory(index => index == 0
            ? new FakeNodeInstance(_ => throw Unavailable())
            : new FakeNodeInstance(_ => Task.FromResult<object>("second")));
        using var services = new NodeServicesImpl(factory.Create);

        Assert.Equal("second", await services.InvokeAsync<string>("module"));
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Does_not_retry_a_second_time()
    {
        // The retry deliberately passes allowRetry: false, so a freshly created instance that also
        // reports itself unavailable surfaces the exception rather than looping forever.
        var factory = new CountingFactory(_ => new FakeNodeInstance(_ => throw Unavailable()));
        using var services = new NodeServicesImpl(factory.Create);

        await Assert.ThrowsAsync<NodeInvocationException>(() => services.InvokeAsync<string>("module"));

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Does_not_retry_when_the_instance_is_still_available()
    {
        var factory = new CountingFactory(_ => new FakeNodeInstance(
            _ => throw new NodeInvocationException("boom", "details")));
        using var services = new NodeServicesImpl(factory.Create);

        await Assert.ThrowsAsync<NodeInvocationException>(() => services.InvokeAsync<string>("module"));

        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Does_not_intercept_exceptions_that_are_not_node_invocation_failures()
    {
        var factory = new CountingFactory(_ => new FakeNodeInstance(
            _ => throw new InvalidOperationException("unrelated")));
        using var services = new NodeServicesImpl(factory.Create);

        await Assert.ThrowsAsync<InvalidOperationException>(() => services.InvokeAsync<string>("module"));

        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Disposes_the_failed_instance_immediately_without_connection_draining()
    {
        FakeNodeInstance? first = null;
        var factory = new CountingFactory(index =>
        {
            if (index == 0)
                return first = new FakeNodeInstance(_ => throw Unavailable(allowConnectionDraining: false));
            return new FakeNodeInstance(_ => Task.FromResult<object>("second"));
        });
        using var services = new NodeServicesImpl(factory.Create);

        await services.InvokeAsync<string>("module");

        Assert.True(first!.Disposed);
    }

    [Fact]
    public async Task Defers_disposal_of_the_failed_instance_when_connection_draining_is_allowed()
    {
        // The draining delay is 15 seconds, so the old instance must still be alive right after the
        // retry. The test asserts the deferral, not the exact duration - waiting it out would make
        // the suite slow for no extra confidence.
        FakeNodeInstance? first = null;
        var factory = new CountingFactory(index =>
        {
            if (index == 0)
                return first = new FakeNodeInstance(_ => throw Unavailable(allowConnectionDraining: true));
            return new FakeNodeInstance(_ => Task.FromResult<object>("second"));
        });
        using var services = new NodeServicesImpl(factory.Create);

        await services.InvokeAsync<string>("module");

        Assert.False(first!.Disposed);
    }

    [Fact]
    public async Task Disposes_the_current_instance_on_dispose()
    {
        FakeNodeInstance? instance = null;
        var factory = new CountingFactory(_ => instance = new FakeNodeInstance(_ => Task.FromResult<object>("ok")));
        var services = new NodeServicesImpl(factory.Create);

        await services.InvokeAsync<string>("module");
        services.Dispose();

        Assert.True(instance!.Disposed);
    }

    [Fact]
    public void Dispose_is_safe_when_no_instance_was_ever_created()
    {
        var factory = new CountingFactory(_ => new FakeNodeInstance(_ => Task.FromResult<object>("ok")));
        var services = new NodeServicesImpl(factory.Create);

        services.Dispose();

        Assert.Equal(0, factory.CreateCount);
    }

    private sealed class CountingFactory(Func<int, INodeInstance> create)
    {
        private int createCount;

        public int CreateCount => Volatile.Read(ref createCount);

        public INodeInstance Create() => create(Interlocked.Increment(ref createCount) - 1);
    }

    private sealed class FakeNodeInstance(Func<NodeInvocationInfo, Task<object>> invoke) : INodeInstance
    {
        public bool Disposed { get; private set; }

        public async Task<T> InvokeExportAsync<T>(CancellationToken cancellationToken, string moduleName, string exportNameOrNull, params object[] args)
        {
            var result = await invoke(new NodeInvocationInfo
            {
                ModuleName = moduleName,
                ExportedFunctionName = exportNameOrNull,
                Args = args,
            });
            return (T)result;
        }

        public void Dispose() => Disposed = true;
    }
}
