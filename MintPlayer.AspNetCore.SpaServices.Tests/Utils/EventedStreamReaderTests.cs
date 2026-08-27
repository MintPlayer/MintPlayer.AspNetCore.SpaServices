using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Utils;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Utils;

/// <summary>
/// <c>EventedStreamReader</c> is how the dev-server's stdout is turned into lines and scanned for the
/// "ready" message. It takes a plain <see cref="StreamReader"/>, so it can be driven from an
/// in-memory stream with no process involved.
/// <para>
/// Note that a completed line <b>keeps its trailing newline</b>. That is easy to get wrong, and the
/// readiness regexes have to tolerate it, so it is asserted explicitly below.
/// </para>
/// </summary>
public class EventedStreamReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Collects every line, then the close notification, without ever sleeping.
    /// <para>
    /// The reader starts consuming the stream from inside its own constructor, so a test that
    /// subscribes afterwards races it and can miss early lines. The gate holds the first read until
    /// the handlers are attached, which makes these tests deterministic rather than merely usually
    /// correct.
    /// </para>
    /// </summary>
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
        // The remainder is flushed when the stream ends, otherwise a dev server that never writes a
        // final newline would have its last line - possibly the "ready" message - swallowed. It is
        // the only line that arrives without a delimiter.
        Assert.Equal(["first\n", "second"], await ReadAllLines("first\nsecond"));
    }

    [Fact]
    public async Task Keeps_both_characters_of_a_crlf_pair()
    {
        // Splitting is on '\n' alone, so a Windows dev server's '\r' survives at the end of the line.
        Assert.Equal(["first\r\n", "second\r\n"], await ReadAllLines("first\r\nsecond\r\n"));
    }

    [Fact]
    public async Task Emits_nothing_for_an_empty_stream()
    {
        Assert.Empty(await ReadAllLines(string.Empty));
    }

    [Fact]
    public async Task Emits_a_bare_newline_for_a_blank_line()
    {
        Assert.Equal(["\n", "after\n"], await ReadAllLines("\nafter\n"));
    }

    [Fact]
    public async Task Reassembles_a_line_that_spans_more_than_one_read_buffer()
    {
        // The internal buffer is 8 KiB, so a longer line proves the partial-line accumulation across
        // reads actually works rather than happening to fit in a single chunk.
        var longLine = new string('x', 20_000);

        Assert.Equal([longLine + "\n", "done\n"], await ReadAllLines($"{longLine}\ndone\n"));
    }

    [Fact]
    public async Task WaitForMatch_completes_with_the_matching_line()
    {
        using var stream = new GatedStream("starting\nopen your browser on http://localhost:4200\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var match = reader.WaitForMatch(new Regex(@"open your browser on (?<openbrowser>http\S+)"));
        stream.Release();

        var result = await match.WaitAsync(Timeout);

        Assert.True(result.Success);
        Assert.Equal("http://localhost:4200", result.Groups["openbrowser"].Value);
    }

    [Fact]
    public async Task WaitForMatch_ignores_ansi_escape_sequences()
    {
        // Dev servers colour their output. Escape sequences are stripped before matching, so a
        // coloured "ready" line is still recognised.
        using var stream = new GatedStream("\x1B[32mopen your browser on http://localhost:4200\x1B[0m\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var match = reader.WaitForMatch(new Regex(@"open your browser on (?<openbrowser>http\S+)"));
        stream.Release();

        var result = await match.WaitAsync(Timeout);

        Assert.Equal("http://localhost:4200", result.Groups["openbrowser"].Value);
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

        // Each line still carries its own '\n' and AppendLine adds another, so the accumulated text
        // is doubly separated. Pinned because it is surprising, and because this text ends up in the
        // exception message when a dev server fails to start.
        Assert.Equal($"first\n{Environment.NewLine}second\n{Environment.NewLine}", stringReader.ReadAsString());
    }

    [Fact]
    public void EventedStreamStringReader_rejects_a_null_reader()
    {
        Assert.Throws<ArgumentNullException>(() => new EventedStreamStringReader(null!));
    }

    [Fact]
    public async Task EventedStreamStringReader_stops_accumulating_once_disposed()
    {
        var closed = new TaskCompletionSource();
        using var stream = new GatedStream("first\nsecond\n");
        var reader = new EventedStreamReader(new StreamReader(stream));
        var stringReader = new EventedStreamStringReader(reader);
        reader.OnStreamClosed += () => closed.TrySetResult();
        stringReader.Dispose();
        stream.Release();

        await closed.Task.WaitAsync(Timeout);

        Assert.Equal(string.Empty, stringReader.ReadAsString());
    }

    /// <summary>
    /// An in-memory stream whose first read blocks until <see cref="Release"/> is called, so a test
    /// can attach handlers before any data flows.
    /// </summary>
    private sealed class GatedStream(string content) : Stream
    {
        private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(content));
        private readonly SemaphoreSlim gate = new(0, 1);
        private bool opened;

        public void Release() => gate.Release();

        private async ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            if (opened) return;
            await gate.WaitAsync(cancellationToken);
            opened = true;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await OpenAsync(cancellationToken);
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
