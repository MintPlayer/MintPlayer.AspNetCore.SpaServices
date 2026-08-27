using MintPlayer.AspNetCore.SpaServices.Utils;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Utils;

public class TaskTimeoutExtensionsTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Immediate = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task Passes_through_a_completed_task()
    {
        await Task.CompletedTask.WithTimeout(Generous, "should not time out");
    }

    [Fact]
    public async Task Returns_the_result_of_a_completed_task()
    {
        Assert.Equal(42, await Task.FromResult(42).WithTimeout(Generous, "should not time out"));
    }

    [Fact]
    public async Task Throws_a_TimeoutException_carrying_the_supplied_message()
    {
        var never = new TaskCompletionSource().Task;

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => never.WithTimeout(Immediate, "the dev server took too long"));

        Assert.Equal("the dev server took too long", ex.Message);
    }

    [Fact]
    public async Task Throws_a_TimeoutException_for_a_generic_task_that_never_completes()
    {
        var never = new TaskCompletionSource<int>().Task;

        await Assert.ThrowsAsync<TimeoutException>(
            () => never.WithTimeout(Immediate, "the dev server took too long"));
    }

    [Fact]
    public async Task Surfaces_a_faulted_task_rather_than_the_timeout()
    {
        var faulted = Task.FromException(new InvalidOperationException("inner failure"));

        // The non-generic overload propagates via Task.Wait, which wraps in an AggregateException.
        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => faulted.WithTimeout(Generous, "unused"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task Surfaces_a_faulted_generic_task_rather_than_the_timeout()
    {
        var faulted = Task.FromException<int>(new InvalidOperationException("inner failure"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => faulted.WithTimeout(Generous, "unused"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
