using MintPlayer.AspNetCore.NodeServices.HostingModels;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

public class NodeInvocationExceptionTests
{
    [Fact]
    public void Joins_the_message_and_the_details_onto_separate_lines()
    {
        var ex = new NodeInvocationException("Something failed", "at Object.<anonymous>");

        Assert.Equal($"Something failed{Environment.NewLine}at Object.<anonymous>", ex.Message);
    }

    [Fact]
    public void Defaults_both_flags_to_false()
    {
        var ex = new NodeInvocationException("m", "d");

        Assert.False(ex.NodeInstanceUnavailable);
        Assert.False(ex.AllowConnectionDraining);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Round_trips_every_meaningful_flag_combination(bool unavailable, bool draining)
    {
        var ex = new NodeInvocationException("m", "d", unavailable, draining);

        Assert.Equal(unavailable, ex.NodeInstanceUnavailable);
        Assert.Equal(draining, ex.AllowConnectionDraining);
    }

    [Fact]
    public void Rejects_connection_draining_without_an_unavailable_instance()
    {
        // Draining only means anything when the instance is being replaced, so the combination is
        // rejected at construction rather than silently ignored later.
        Assert.Throws<ArgumentException>(
            () => new NodeInvocationException("m", "d", nodeInstanceUnavailable: false, allowConnectionDraining: true));
    }
}
