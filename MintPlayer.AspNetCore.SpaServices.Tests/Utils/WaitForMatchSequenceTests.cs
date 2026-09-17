using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Utils;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Utils;

/// <summary>
/// Regression cover for the race that made sequential <c>WaitForMatch</c> calls hang.
/// </summary>
/// <remarks>
/// Before the fix the reader emitted every line of a chunk back to back, and a second
/// <c>WaitForMatch</c> issued from the continuation of the first could subscribe after its line - and
/// the stream-closed notification - had already fired. Its task then never completed and the caller
/// waited forever. A <see cref="MemoryStream"/> delivers everything in one read, so these tests lose
/// that race every time, which is exactly what makes them a regression test.
/// </remarks>
public class WaitForMatchSequenceTests
{
	private static EventedStreamReader ReaderOver(string content, CancellationToken cancellationToken = default)
		=> new(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(content))), cancellationToken);

	[Fact]
	public async Task Finds_a_second_match_that_arrived_in_the_same_chunk()
	{
		var reader = ReaderOver("Build at: step one\nopen your browser on http://localhost:4200/\n");

		var first = await reader.WaitForMatch(new Regex(@"Build at: (?<step>\w+)"));
		var second = await reader.WaitForMatch(new Regex(@"open your browser on (?<openbrowser>http\S+)"));

		Assert.Equal("step", first.Groups["step"].Value);
		Assert.Equal("http://localhost:4200/", second.Groups["openbrowser"].Value);
	}

	[Fact]
	public async Task Counts_repeated_occurrences_rather_than_rematching_one_line()
	{
		// AngularPrerendererBuilder waits for the same regex `occurrences` times. If a match were
		// re-satisfied from the same line, the second wait would return instantly and the builder
		// would declare a half-finished build ready.
		var reader = ReaderOver("Build at: 1\nBuild at: 2\nBuild at: 3\n");
		var regex = new Regex(@"Build at: (?<n>\d+)");

		Assert.Equal("1", (await reader.WaitForMatch(regex)).Groups["n"].Value);
		Assert.Equal("2", (await reader.WaitForMatch(regex)).Groups["n"].Value);
		Assert.Equal("3", (await reader.WaitForMatch(regex)).Groups["n"].Value);
	}

	[Fact]
	public async Task Fails_fast_once_the_stream_has_closed()
	{
		var reader = ReaderOver("only line\n");
		await reader.WaitForMatch(new Regex("only line"));

		// The stream is finished, so nothing can ever match. Failing beats hanging.
		await Assert.ThrowsAsync<EndOfStreamException>(() => reader.WaitForMatch(new Regex("never appears")));
	}

	[Fact]
	public async Task Skips_a_line_an_earlier_wait_already_consumed()
	{
		var reader = ReaderOver("alpha\nbeta\nalpha\n");

		await reader.WaitForMatch(new Regex("alpha"));
		var next = await reader.WaitForMatch(new Regex("alpha"));

		// The second wait must find the SECOND alpha, and the beta between them must not confuse it.
		Assert.True(next.Success);
		await Assert.ThrowsAsync<EndOfStreamException>(() => reader.WaitForMatch(new Regex("alpha")));
	}

	[Fact]
	public async Task Strips_ansi_colours_before_matching_history()
	{
		// The live path already stripped colours; the history scan has to agree, or a match would
		// depend on whether the caller happened to win the race.
		var reader = ReaderOver("[32mBuild at: 1[0m\nplain\n");

		var match = await reader.WaitForMatch(new Regex(@"^Build at: (?<n>\d+)"));

		Assert.Equal("1", match.Groups["n"].Value);
	}

	[Fact]
	public async Task Releases_a_pending_wait_when_the_token_is_cancelled()
	{
		// A reader over a stream that never ends would otherwise keep the caller waiting for ever.
		using var cts = new CancellationTokenSource();
		var reader = new EventedStreamReader(new StreamReader(new NeverEndingStream()), cts.Token);

		var pending = reader.WaitForMatch(new Regex("never appears"));
		await cts.CancelAsync();

		await Assert.ThrowsAsync<EndOfStreamException>(() => pending);
	}

	/// <summary>A stream that blocks until cancelled, standing in for a live dev server's stdout.</summary>
	private sealed class NeverEndingStream : Stream
	{
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return 0;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
