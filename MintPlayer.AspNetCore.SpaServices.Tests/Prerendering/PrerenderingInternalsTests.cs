using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Extensions;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Internals;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// The Prerendering package carries its own copies of <c>EventedStreamReader</c>,
/// <c>EventedStreamStringReader</c> and <c>TaskTimeoutExtensions</c>, duplicated from
/// <c>MintPlayer.AspNetCore.SpaServices</c>. They are separate types in separate assemblies, so the
/// tests over the originals do not exercise a single line of them.
/// <para>
/// These tests assert that the copies behave identically to the originals. If someone consolidates
/// the duplication, this file should collapse into the original suite; until then it is what stops
/// the two copies drifting apart silently.
/// </para>
/// </summary>
public class PrerenderingEventedStreamReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task<List<string>> ReadAllLines(string content)
    {
        var lines = new List<string>();
        var closed = new TaskCompletionSource();

        using var stream = new GatedStream(content);
        var reader = new EventedStreamReader(new StreamReader(stream));
        reader.OnReceivedLine += line => lines.Add(line);
        reader.OnStreamClosed += () => closed.TrySetResult();
        stream.Release();

        await closed.Task.WaitAsync(Timeout);
        return lines;
    }

    [Fact]
    public async Task Splits_content_into_lines_keeping_the_newline()
    {
        Assert.Equal(["first\n", "second\n", "third\n"], await ReadAllLines("first\nsecond\nthird\n"));
    }

    [Fact]
    public async Task Emits_a_trailing_line_that_has_no_newline()
    {
        Assert.Equal(["first\n", "second"], await ReadAllLines("first\nsecond"));
    }

    [Fact]
    public async Task Keeps_both_characters_of_a_crlf_pair()
    {
        Assert.Equal(["first\r\n", "second\r\n"], await ReadAllLines("first\r\nsecond\r\n"));
    }

    [Fact]
    public async Task Emits_nothing_for_an_empty_stream()
    {
        Assert.Empty(await ReadAllLines(string.Empty));
    }

    [Fact]
    public async Task Reassembles_a_line_that_spans_more_than_one_read_buffer()
    {
        var longLine = new string('x', 20_000);

        Assert.Equal([longLine + "\n", "done\n"], await ReadAllLines($"{longLine}\ndone\n"));
    }

    [Fact]
    public async Task WaitForMatch_completes_with_the_matching_line()
    {
        // This copy is the one that watches the Angular SSR build, so the pattern here is the
        // build-finished marker rather than the dev-server's browser URL.
        using var stream = new GatedStream("chunk {0}\nBuild at: 2026-08-27\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var match = reader.WaitForMatch(new Regex(@"Build at\:"));
        stream.Release();

        Assert.True((await match.WaitAsync(Timeout)).Success);
    }

    [Fact]
    public async Task WaitForMatch_ignores_ansi_escape_sequences()
    {
        using var stream = new GatedStream("\x1B[32mBuild at: 2026-08-27\x1B[0m\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var match = reader.WaitForMatch(new Regex(@"Build at\:"));
        stream.Release();

        Assert.True((await match.WaitAsync(Timeout)).Success);
    }

    [Fact]
    public async Task WaitForMatch_fails_when_the_stream_closes_without_a_match()
    {
        using var stream = new GatedStream("nothing interesting here\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var match = reader.WaitForMatch(new Regex("never-appears"));
        stream.Release();

        await Assert.ThrowsAsync<EndOfStreamException>(() => match.WaitAsync(Timeout));
    }

    [Fact]
    public void Rejects_a_null_stream_reader()
    {
        Assert.Throws<ArgumentNullException>(() => new EventedStreamReader(null!));
    }

    [Fact]
    public async Task EventedStreamStringReader_accumulates_the_whole_stream()
    {
        var closed = new TaskCompletionSource();
        using var stream = new GatedStream("first\nsecond\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        using var stringReader = new EventedStreamStringReader(reader);
        reader.OnStreamClosed += () => closed.TrySetResult();
        stream.Release();

        await closed.Task.WaitAsync(Timeout);

        Assert.Equal($"first\n{Environment.NewLine}second\n{Environment.NewLine}", stringReader.ReadAsString());
    }

    [Fact]
    public void EventedStreamStringReader_rejects_a_null_reader()
    {
        Assert.Throws<ArgumentNullException>(() => new EventedStreamStringReader(null!));
    }

    /// <summary>
    /// Holds the first read until <see cref="Release"/> is called, so handlers can be attached before
    /// any data flows. The reader starts consuming inside its own constructor, so without this the
    /// tests would race it.
    /// </summary>
    private sealed class GatedStream(string content) : Stream
    {
        private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(content));
        private readonly SemaphoreSlim gate = new(0, 1);
        private bool opened;

        public void Release() => gate.Release();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!opened)
            {
                await gate.WaitAsync(cancellationToken);
                opened = true;
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!opened)
            {
                gate.Wait();
                opened = true;
            }
            return inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                gate.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public class PrerenderingTaskTimeoutExtensionsTests
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
            () => never.WithTimeout(Immediate, "the SSR build took too long"));

        Assert.Equal("the SSR build took too long", ex.Message);
    }

    [Fact]
    public async Task Throws_a_TimeoutException_for_a_generic_task_that_never_completes()
    {
        var never = new TaskCompletionSource<int>().Task;

        await Assert.ThrowsAsync<TimeoutException>(() => never.WithTimeout(Immediate, "too slow"));
    }

    [Fact]
    public async Task Surfaces_a_faulted_task_rather_than_the_timeout()
    {
        var faulted = Task.FromException(new InvalidOperationException("inner failure"));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => faulted.WithTimeout(Generous, "unused"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
